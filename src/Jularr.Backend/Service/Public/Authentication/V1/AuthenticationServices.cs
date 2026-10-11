using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Account;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Logic.Account;
using Jularr.Backend.Logic.Session;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;

namespace Jularr.Backend.Service.Public.Authentication.V1
{
    [ServiceEndpoint("GET", "/api/v1/auth")]
    public sealed class ReadAuthenticationV1 : PublicService<AuthenticationParametersV1>
    {
        private readonly AuthenticationOptions _options;
        private readonly SessionAuthentication _sessions;
        private readonly IHttpContextAccessor _http;

        public ReadAuthenticationV1(IOptions<AuthenticationOptions> options, SessionAuthentication sessions, IHttpContextAccessor http)
        {
            _options = options.Value;
            _sessions = sessions;
            _http = http;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() =>
            [ServiceResultType.Default<AuthenticationResultV1>()];
        protected override ModuleRequirement GetInstanceModules(AuthenticationParametersV1 parameters) => ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnRead()
        {
            long? accountId = null;
            long? profileId = null;
            var principal = _http.HttpContext?.User;

            if (principal?.Identity?.IsAuthenticated == true)
            {
                var session = await _sessions.ReadRequestAsync(_http.HttpContext, CancellationToken);
                accountId = session.AccountId;
                profileId = session.ProfileId;
            }

            return new AuthenticationResultV1(_options.PlexLoginEnabled,
                _options.PlexLoginEnabled && _options.PlexRegistrationEnabled, accountId, profileId);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/auth/login")]
    public sealed class LoginV1 : PublicService<AuthenticationParametersV1, LoginDataV1>
    {
        private readonly AuthenticationOptions _options;

        public LoginV1(IOptions<AuthenticationOptions> options)
        {
            _options = options.Value;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LoginResultV1>()];
        protected override ModuleRequirement GetInstanceModules(AuthenticationParametersV1 parameters) => ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnCreate()
        {
            var account = await Context.AuthenticatePasswordAsync(new AccountEmailParameters(Data.Email), Data.Password, CancellationToken);
            var session = await Context.CreateSessionAsync(account, TimeSpan.FromHours(_options.SessionLifetimeHours), CancellationToken);

            return new LoginResultV1(session);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/auth/register")]
    public sealed class RegisterV1 : PublicService<AuthenticationParametersV1, RegisterDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<RegisterResultV1>()];
        protected override ModuleRequirement GetInstanceModules(AuthenticationParametersV1 parameters) => ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnCreate()
        {
            var account = new AccountCreationData(Data.Email, Data.DisplayName, false, null);
            await Context.CreateRegistrationRequestAsync(account, Data.Password, Data.UiLocaleId, CancellationToken);

            return new RegisterResultV1();
        }
    }
}
