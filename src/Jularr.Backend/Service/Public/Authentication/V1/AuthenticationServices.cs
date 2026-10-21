using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Logic.Account;
using Jularr.Backend.Logic.Session;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Service.Public.Authentication.V1
{
    [ServiceEndpoint("GET", "/api/v1/auth")]
    public sealed class ReadAuthenticationV1 : PublicService<AuthenticationParametersV1>
    {
        private readonly SessionAuthentication _sessions;
        private readonly IHttpContextAccessor _http;

        internal ReadAuthenticationV1(SessionAuthentication sessions, IHttpContextAccessor http)
        {
            _sessions = sessions;
            _http = http;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() =>
            [ServiceResultType.Default<AuthenticationResultV1>()];
        protected override ModuleRequirement GetInstanceModules(AuthenticationParametersV1 parameters) => ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnRead()
        {
            // Resolve optional signed-in state.
            long? accountId = null;
            long? profileId = null;
            long? uiLocaleId = null;
            IReadOnlyList<string> permissions = [];
            var principal = _http.HttpContext?.User;

            if (principal?.Identity?.IsAuthenticated == true)
            {
                var session = await _sessions.ReadRequestAsync(_http.HttpContext, CancellationToken);
                accountId = session.AccountId;
                profileId = session.ProfileId;
                uiLocaleId = session.UiLocaleId;
                permissions = ServiceGate.GetPermissions(session.AccountId, session.ProfileId, session.ProfileOwnerAccountId, session.AccountRoleTypeId, session.IsDevice);
            }

            // Expose available login methods.
            var plex = await Context.ReadPlexSignInAsync(CancellationToken);
            return new AuthenticationResultV1(plex.SignIn, plex.CanRegister, accountId, profileId, uiLocaleId, permissions);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/auth/login")]
    public sealed class LoginV1 : PublicService<AuthenticationParametersV1, LoginDataV1>
    {
        private readonly AuthenticationOptions _options;

        internal LoginV1(IOptions<AuthenticationOptions> options)
        {
            _options = options.Value;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LoginResultV1>()];
        protected override ModuleRequirement GetInstanceModules(AuthenticationParametersV1 parameters) => ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnCreate()
        {
            // Verify credentials under lock.
            var account = await Context.AuthenticatePasswordAsync(new AccountLoginParameters(Data.Username), Data.Password, CancellationToken);

            // Issue the session atomically.
            var session = await Context.CreateSessionAsync(account, TimeSpan.FromHours(_options.SessionLifetimeHours), CancellationToken);

            return new LoginResultV1(session);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/auth/register")]
    public sealed class RegisterV1 : PublicService<AuthenticationParametersV1, RegisterDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<RegisterResultV1>()];
        protected override ModuleRequirement GetInstanceModules(AuthenticationParametersV1 parameters) => ModuleRequirement.All("Registration");

        protected override async Task<IServiceOutput> OnCreate()
        {
            // Request approval, never auto-enable; the profile is named after the username until its owner renames it.
            var account = new AccountCreationData(Data.Username, null, Data.Username, false, null);
            await Context.CreateRegistrationRequestAsync(account, Data.Password, Data.UiLocaleId, CancellationToken);

            return new RegisterResultV1();
        }
    }
}
