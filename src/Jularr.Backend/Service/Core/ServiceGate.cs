using System.Security;
using Microsoft.AspNetCore.Http;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Session;

namespace Jularr.Backend.Service.Core
{
    public sealed class ServiceGate : IServiceGate
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

        public Task RequireModulesAsync(ModuleRequirement requirement, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requirement.Mode != ModuleRequirementMode.None)
            {
                throw new UnauthorizedAccessException("No instance modules are configured.");
            }

            return Task.CompletedTask;
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

            if (area != ServiceArea.User)
            {
                throw new UnauthorizedAccessException("This service area is unavailable.");
            }

            var session = await _sessions.ReadRequestAsync(_accessor.HttpContext, cancellationToken);
            if (session.ProfileId is null)
            {
                throw new UnauthorizedAccessException("An active profile is required for user services.");
            }

            var parameters = _sessions.Parameters!;
            _caller = new ServiceCaller(session.AccountId, session.ProfileId, area,
                parameters.SessionPublicId.ToString("D"), parameters.SessionPublicId, parameters.SessionRevision,
                session.ProfileOwnerAccountId, parameters.SessionTokenHash);
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
                || current.ProfileOwnerAccountId != caller.ProfileOwnerAccountId)
            {
                throw new UnauthorizedAccessException("The profile session changed.");
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

            // Grant implemented permissions only.
            if (caller.Area == ServiceArea.User && permission.Key is "profile.self.read" or "profile.self.update")
            {
                if (permission.Key == "profile.self.update" && caller.ProfileOwnerAccountId != caller.AccountId)
                {
                    throw new SecurityException("The requested permission is unavailable.");
                }

                return;
            }

            throw new SecurityException("The requested permission is unavailable.");
        }

    }
}
