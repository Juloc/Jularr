using System.Security;
using Microsoft.AspNetCore.Http;
using Jularr.Backend.Data.Enums.Service;
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
        private ServiceCaller? _caller;

        public ServiceGate(SessionAuthentication sessions, IHttpContextAccessor accessor)
        {
            _sessions = sessions;
            _accessor = accessor;
        }

        public async Task RequireModulesAsync(ModuleRequirement requirement, LogicContext context, bool canMutate, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requirement.Mode == ModuleRequirementMode.None)
            {
                return;
            }

            var parameters = new InstanceModuleParameters(requirement.ModuleKeys.ToArray());
            IReadOnlyList<InstanceModuleData> modules;
            if (canMutate)
            {
                modules = await context.ReadInstanceModulesAsync(parameters, cancellationToken);
            }
            else
            {
                // Read without mutation locks.
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        module."Key",
                        module."IsEnabled"
                    FROM
                        "InstanceModules" AS module
                    WHERE
                        module."Key" = ANY(@ModuleKeys);
                    """;
                sql.Parameters.Add(parameters);
                modules = await context.ReadSql.ReadListAsync<InstanceModuleData>(sql, cancellationToken);
            }

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

            if (area is not (ServiceArea.User or ServiceArea.Admin))
            {
                throw new UnauthorizedAccessException("This service area is unavailable.");
            }

            var session = await _sessions.ReadRequestAsync(_accessor.HttpContext, cancellationToken);
            if (area == ServiceArea.Admin && session.AccountRoleTypeId == AccountRoleType.User)
            {
                throw new UnauthorizedAccessException("An administrative role is required.");
            }

            var parameters = _sessions.Parameters!;
            _caller = new ServiceCaller(session.AccountId, session.ProfileId, area,
                parameters.SessionPublicId.ToString("D"), parameters.SessionPublicId, parameters.SessionRevision,
                session.ProfileOwnerAccountId, parameters.SessionTokenHash, session.AccountRoleTypeId);
            return _caller;
        }

        public Task RequireAccessAsync(ServiceCaller caller, ServicePermission permission, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequirePermission(caller, permission);

            return Task.CompletedTask;
        }

        public async Task RequireTransactionalAccessAsync(ServiceCaller caller, ServicePermission permission, LogicContext context, CancellationToken cancellationToken)
        {
            RequirePermission(caller, permission);

            if (caller.Area == ServiceArea.Public)
            {
                return;
            }

            // Lock mutable session state.
            var parameters = _sessions.Parameters ?? throw new UnauthorizedAccessException("The session is unavailable.");
            var current = await context.ReadSessionAsync(parameters, cancellationToken);

            if (current.AccountId != caller.AccountId || current.ProfileId != caller.ProfileId
                || current.ProfileOwnerAccountId != caller.ProfileOwnerAccountId || current.AccountRoleTypeId != caller.AccountRoleTypeId)
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

            // Resolve declared permissions.
            if (caller.Area is ServiceArea.User or ServiceArea.Admin)
            {
                if (permission.Key.StartsWith("profile.", StringComparison.Ordinal) && caller.ProfileId is null)
                {
                    throw new UnauthorizedAccessException("An active profile is required.");
                }

                if (GetPermissions(caller.AccountId, caller.ProfileId, caller.ProfileOwnerAccountId, caller.AccountRoleTypeId).Contains(permission.Key, StringComparer.Ordinal))
                {
                    return;
                }
            }

            throw new SecurityException("The requested permission is unavailable.");
        }

        // Share effective grants with consumers.
        internal static IReadOnlyList<string> GetPermissions(long? accountId, long? profileId, long? ownerAccountId, AccountRoleType accountRoleTypeId = AccountRoleType.User)
        {
            if (accountId is null)
            {
                return [];
            }

            var permissions = new List<string> { "session.self.read", "session.self.revoke", "session.self.logout", "session.profile.read", "session.profile.select", "instance.capabilities.read" };

            // Manual media management only.
            if (accountRoleTypeId is AccountRoleType.Owner or AccountRoleType.MediaManager)
            {
                permissions.AddRange(["admin.access", "media.read", "media.manual.update", "requests.read", "requests.update", "wanted.read", "wanted.update"]);
            }

            // Instance administration stays restricted.
            if (accountRoleTypeId == AccountRoleType.Owner)
            {
                permissions.AddRange(["accounts.read", "accounts.update", "storage.read", "storage.update", "providers.read", "providers.update",
                    "localization.read", "localization.update", "rules.read", "rules.update", "acquisition.manage", "instance.settings.read", "instance.settings.update"]);
            }

            if (profileId is not null)
            {
                permissions.Add("profile.self.read");
                permissions.AddRange(["profile.media.read", "profile.watchlist.update", "profile.requests.read", "profile.requests.update"]);
                if (ownerAccountId == accountId)
                {
                    permissions.Add("profile.self.update");
                }
            }

            return permissions.AsReadOnly();
        }

    }
}
