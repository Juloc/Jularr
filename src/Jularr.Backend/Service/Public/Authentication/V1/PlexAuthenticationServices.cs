using System.Security;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Authentication;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Integrations.Data.Plex;
using Jularr.Integrations.Logic.Plex;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Authentication;
using Jularr.Backend.Service.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;

namespace Jularr.Backend.Service.Public.Authentication.V1
{
    [ServiceEndpoint("POST", "/api/v1/auth/plex")]
    public sealed class StartPlexV1 : PublicService<AuthenticationParametersV1, NoData>
    {
        private readonly PlexClient _plex;
        private readonly IDataProtector _protector;

        internal StartPlexV1(PlexClient plex, IDataProtectionProvider protection)
        {
            _plex = plex;
            _protector = protection.CreateProtector("Jularr.Plex.DeviceKey.V1");
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<StartPlexResultV1>()];
        protected override ModuleRequirement GetInstanceModules(AuthenticationParametersV1 parameters) => ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (!(await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadPlexSignInAsync(ct), CancellationToken)).SignIn)
            {
                throw new SecurityException("Plex sign-in is disabled.");
            }

            // Create browser-bound device proof.
            var publicId = Guid.NewGuid();
            var clientIdentifier = "jularr-" + publicId.ToString("N");
            var nonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

            // Request the external PIN.
            var pin = await _plex.CreatePinAsync(clientIdentifier, CancellationToken);
            var expiresAt = DateTime.UtcNow.AddMinutes(5);

            // Protect the signing key.
            byte[] protectedKey;
            try
            {
                protectedKey = _protector.Protect(pin.PrivateKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pin.PrivateKey);
            }

            var challenge = new PlexChallengeCreationData(publicId, SHA256.HashData(Encoding.UTF8.GetBytes(nonce)), protectedKey, expiresAt);

            // Persist after external I/O.
            await Context.ExecuteBatchAsync(async (context, token) =>
            {
                await context.CreatePlexFlowAsync(challenge, pin.Id, clientIdentifier, token);
                return true;
            }, CancellationToken);

            var url = "https://app.plex.tv/auth#?clientID=" + Uri.EscapeDataString(clientIdentifier)
                + "&code=" + Uri.EscapeDataString(pin.Code) + "&context%5Bdevice%5D%5Bproduct%5D=Jularr";
            return new StartPlexResultV1(publicId, url, nonce, expiresAt);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/auth/plex/{authChallengePublicId:guid}")]
    public sealed class CompletePlexV1 : PublicService<CompletePlexParametersV1, NoData>
    {
        private readonly PlexClient _plex;
        private readonly IDataProtector _protector;
        private readonly AuthenticationOptions _options;
        private readonly IHttpContextAccessor _http;

        internal CompletePlexV1(PlexClient plex, IDataProtectionProvider protection, IOptions<AuthenticationOptions> options, IHttpContextAccessor http)
        {
            _plex = plex;
            _protector = protection.CreateProtector("Jularr.Plex.DeviceKey.V1");
            _options = options.Value;
            _http = http;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LoginResultV1>()];
        protected override ModuleRequirement GetInstanceModules(CompletePlexParametersV1 parameters) => ModuleRequirement.None;

        protected override async Task<IServiceOutput> OnExecute()
        {
            var settings = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadPlexSignInAsync(ct), CancellationToken);
            if (!settings.SignIn)
            {
                throw new SecurityException("Plex sign-in is disabled.");
            }

            // Match the initiating browser.
            var nonce = _http.HttpContext?.Request.Cookies[ServiceApi.AuthenticationNonceCookieName]
                ?? throw new UnauthorizedAccessException("The authentication attempt is unavailable.");
            var parameters = new PlexChallengeParameters(Parameters.AuthChallengePublicId, nonce);
            var flow = await Context.ExecuteBatchAsync(
                (context, token) => context.ReadPlexFlowAsync(parameters, token), CancellationToken);

            // Restore the device key.
            byte[] privateKey;
            try
            {
                privateKey = _protector.Unprotect(flow.ProtectedState);
            }
            catch (CryptographicException)
            {
                throw new UnauthorizedAccessException("The authentication attempt is unavailable.");
            }

            // Poll outside database transactions.
            PlexIdentity? identity;
            try
            {
                identity = await _plex.ReadIdentityAsync(flow.ExternalPinId, flow.ClientIdentifier, privateKey, CancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }

            if (identity is null)
            {
                return new LoginResultV1(null);
            }

            // Registration alone requires library access.
            var needsRegistration = await Context.ExecuteBatchAsync(async (context, token) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT NOT EXISTS (
                        SELECT 1
                        FROM "AccountExternalLogins" AS identity
                        WHERE identity."ProviderId" = @ProviderId
                            AND identity."ExternalAccountId" = @ExternalAccountId
                    );
                    """;
                sql.Parameters.Add(new ExternalLoginParameters(flow.ProviderId, identity.ExternalAccountId));
                return (bool)(await context.ReadSql.ExecuteScalarAsync(sql, token))!;
            }, CancellationToken);

            // Users of the connected server's chosen libraries may register.
            var hasAccess = needsRegistration
                && settings.CanRegister
                && await _plex.HasLibraryAccessAsync(flow.ClientIdentifier, identity.AccessToken,
                    new Uri(settings.BaseUrl!), settings.ServerIdentifier!, settings.RegistrationSections, CancellationToken);
            var registration = new PlexRegistrationData(hasAccess, settings.RegistrationApproval, settings.UiLocaleId);

            // Validate the database identity.
            var identityData = new PlexIdentityData(identity.ExternalAccountId, identity.Email, identity.DisplayName, identity.EmailVerified);
            identityData.Validate();

            // Complete atomically.
            var completed = await Context.ExecuteBatchAsync(
                (context, token) => context.CompletePlexAsync(parameters, identityData, registration,
                    TimeSpan.FromHours(_options.SessionLifetimeHours), BrowserDevice.Current(_http.HttpContext), token), CancellationToken);

            return new LoginResultV1(completed.Session, completed.RequiresApproval);
        }
    }
}
