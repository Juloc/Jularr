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
        private readonly AuthenticationOptions _options;
        private readonly SessionAuthentication _sessions;
        private readonly IHttpContextAccessor _http;

        internal ReadAuthenticationV1(IOptions<AuthenticationOptions> options, SessionAuthentication sessions, IHttpContextAccessor http)
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
                permissions = ServiceGate.GetPermissions(session.AccountId, session.ProfileId, session.ProfileOwnerAccountId);
            }

            // Expose available login methods.
            var plexEnabled = false;
            if (_options.PlexLoginEnabled)
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT EXISTS (
                        SELECT 1
                        FROM "Providers" AS provider
                        WHERE provider."Key" = 'plex'
                            AND provider."IsEnabled" = TRUE
                    );
                    """;
                plexEnabled = (bool)(await Context.ReadSql.ExecuteScalarAsync(sql, CancellationToken))!;
            }

            return new AuthenticationResultV1(plexEnabled,
                plexEnabled && _options.PlexRegistrationEnabled, accountId, profileId, uiLocaleId, permissions);
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
            var account = await Context.AuthenticatePasswordAsync(new AccountEmailParameters(Data.Email), Data.Password, CancellationToken);

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
        protected override ModuleRequirement GetInstanceModules(AuthenticationParametersV1 parameters) => ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnCreate()
        {
            // Request approval, never auto-enable.
            var account = new AccountCreationData(Data.Email, Data.DisplayName, false, null);
            await Context.CreateRegistrationRequestAsync(account, Data.Password, Data.UiLocaleId, CancellationToken);

            return new RegisterResultV1();
        }
    }
}
