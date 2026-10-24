using System.Security;
using Microsoft.AspNetCore.Http;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Logic.Session;

namespace Jularr.Backend.Service.Core
{
    internal sealed class ServiceGate : IServiceGate
    {
        public const string SessionClaimType = "jularr.session_id";
        public const string SessionRevisionClaimType = "jularr.session_revision";
        public const string SessionTokenClaimType = "jularr.session_token";

        private readonly SessionAuthentication _sessions;
        private readonly IHttpContextAccessor _accessor;
        private readonly UserActivity _activity;
        private ServiceCaller? _caller;

        public ServiceGate(SessionAuthentication sessions, IHttpContextAccessor accessor, UserActivity activity)
        {
            _sessions = sessions;
            _accessor = accessor;
            _activity = activity;
        }

        public async Task RequireModulesAsync(ModuleRequirement requirement, LogicContext context, bool canMutate, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requirement.Mode == ModuleRequirementMode.None)
            {
                return;
            }

            var parameters = new InstanceModuleParameters(requirement.ModuleKeys.ToArray());
            // Reads take no mutation locks.
            var modules = canMutate
                ? await context.ReadInstanceModulesAsync(parameters, cancellationToken)
                : await context.ReadModuleStatesAsync(parameters, cancellationToken);

            var enabledCount = modules.Count(module => module.IsEnabled);
            if (requirement.Mode == ModuleRequirementMode.All ? enabledCount != requirement.ModuleKeys.Count : enabledCount == 0)
            {
                throw new SecurityException("The requested instance module is unavailable.");
            }
        }

        public async Task<ServiceCaller> RequireCallerAsync(ServiceArea area, CancellationToken cancellationToken)
        {
            _caller = null;
            if (area == ServiceArea.Public)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _caller = new ServiceCaller(null, null, area, "anonymous");
                return _caller;
            }

            // Background work runs outside HTTP requests.
            if (area == ServiceArea.System)
            {
                if (_accessor.HttpContext is not null)
                {
                    throw new UnauthorizedAccessException("System services are unavailable to requests.");
                }

                _caller = new ServiceCaller(null, null, area, "system");
                return _caller;
            }

            if (area is not (ServiceArea.User or ServiceArea.Admin))
            {
                throw new UnauthorizedAccessException("This service area is unavailable.");
            }

            var session = await _sessions.ReadRequestAsync(_accessor.HttpContext, cancellationToken);
            if (_accessor.HttpContext is { } http)
            {
                ServiceError.ShowDetailTo(http, session.AccountRoleTypeId != AccountRoleType.User);
            }
            if (area == ServiceArea.Admin && session.AccountRoleTypeId == AccountRoleType.User)
            {
                throw new UnauthorizedAccessException("An administrative role is required.");
            }

            // A signed-in request counts as use of Jularr.
            _activity.Touch();
            var parameters = _sessions.Parameters!;
            _caller = new ServiceCaller(session.AccountId, session.ProfileId, area,
                parameters.SessionPublicId.ToString("D"), parameters.SessionPublicId, parameters.SessionRevision,
                session.ProfileOwnerAccountId, parameters.SessionTokenHash, session.AccountRoleTypeId, session.IsDevice);
            return _caller;
        }

        public Task RequireAccessAsync(ServiceCaller caller, ServicePermission permission, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequirePermission(caller, permission);

            return Task.CompletedTask;
        }

        public async Task RequireTransactionalAccessAsync(ServiceCaller caller, ServicePermission permission, LogicContext context, CancellationToken cancellationToken, SqlAccessMode mode = SqlAccessMode.ReadWrite)
        {
            RequirePermission(caller, permission);

            if (caller.Area is ServiceArea.Public or ServiceArea.System)
            {
                return;
            }

            // Revalidate reads; lock writes.
            var parameters = _sessions.Parameters ?? throw new UnauthorizedAccessException("The session is unavailable.");
            var current = mode == SqlAccessMode.ReadOnly
                ? await context.ReadSessionProofAsync(parameters, cancellationToken)
                : await context.ReadSessionAsync(parameters, cancellationToken);

            if (current.AccountId != caller.AccountId || current.ProfileId != caller.ProfileId
                || current.ProfileOwnerAccountId != caller.ProfileOwnerAccountId || current.AccountRoleTypeId != caller.AccountRoleTypeId || current.IsDevice != caller.IsDevice)
            {
                throw new UnauthorizedAccessException("The session authorization changed.");
            }
        }

        private void RequirePermission(ServiceCaller caller, ServicePermission permission)
        {
            if (_caller != caller)
            {
                throw new SecurityException("The caller has not been verified.");
            }

            if (caller.Area == ServiceArea.Public && permission == ServicePermission.Public)
            {
                return;
            }

            if (caller.Area == ServiceArea.System && permission.Key.StartsWith("system.", StringComparison.Ordinal))
            {
                return;
            }

            // Resolve declared permissions.
            if (caller.Area is ServiceArea.User or ServiceArea.Admin)
            {
                if (permission.Key.StartsWith("profile.", StringComparison.Ordinal) && caller.ProfileId is null)
                {
                    throw new UnauthorizedAccessException("An active profile is required.");
                }

                if (GetPermissions(caller.AccountId, caller.ProfileId, caller.ProfileOwnerAccountId, caller.AccountRoleTypeId, caller.IsDevice).Contains(permission.Key, StringComparer.Ordinal))
                {
                    return;
                }
            }

            throw new SecurityException("The requested permission is unavailable.");
        }

        // Share effective grants with consumers.
        internal static IReadOnlyList<string> GetPermissions(long? accountId, long? profileId, long? ownerAccountId, AccountRoleType accountRoleTypeId = AccountRoleType.User, bool isDevice = false)
        {
            if (accountId is null)
            {
                return [];
            }

            var permissions = new List<string> { "session.self.read", "session.self.manage", "session.self.logout", "session.profile.read", "session.profile.select",
                "instance.capabilities.read" };

            // A paired device switches profiles but neither creates nor deletes them.
            if (!isDevice)
            {
                permissions.Add("session.profile.create");
            }

            // Manual media management only.
            if (accountRoleTypeId is AccountRoleType.Owner or AccountRoleType.MediaManager)
            {
                permissions.AddRange(["admin.access", "media.read", "media.manual.update", "requests.read", "requests.update", "wanted.read", "wanted.update"]);
            }

            // Instance administration stays restricted.
            if (accountRoleTypeId == AccountRoleType.Owner)
            {
                permissions.AddRange(["accounts.read", "accounts.update", "storage.read", "storage.update", "providers.read", "providers.update",
                    "localization.read", "localization.update", "rules.read", "rules.update", "acquisition.manage", "instance.settings.read", "instance.settings.update",
                    "backup.read", "backup.update"]);
            }

            if (profileId is not null)
            {
                permissions.Add("profile.self.read");
                permissions.AddRange(["profile.media.read", "profile.watchlist.update", "profile.requests.read", "profile.requests.update", "profile.playback", "profile.notifications", "profile.collections"]);

                // A paired device pairs no further ones.
                if (!isDevice)
                {
                    permissions.Add("profile.devices");
                }
                if (ownerAccountId == accountId)
                {
                    permissions.Add("profile.self.update");
                    if (!isDevice)
                    {
                        permissions.Add("profile.self.delete");
                    }
                }
            }

            return permissions.AsReadOnly();
        }

    }
}
