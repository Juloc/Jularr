using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Authentication;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Logic.Authentication;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Plex;
using Jularr.Integrations.Logic.Plex;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Jularr.Backend.Service.Admin.Provider.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/providers/plex/pin")]
    public sealed class StartPlexServerPinV1 : AdminService<PlexServerParametersV1, SavePlexServerDataV1>
    {
        internal const string ProtectorPurpose = "Jularr.Plex.ServerPin.V1";
        private readonly PlexClient _plex;
        private readonly IDataProtector _protector;

        internal StartPlexServerPinV1(PlexClient plex, IDataProtectionProvider protection)
        {
            _plex = plex;
            _protector = protection.CreateProtector(ProtectorPurpose);
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<StartPlexResultV1>()];
        protected override ModuleRequirement GetInstanceModules(PlexServerParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(PlexServerParametersV1 parameters, Type resultType) => new("providers.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var address = ProviderLogic.ServerAddress(Data.BaseUrl, nameof(Data.BaseUrl));
            var publicId = Guid.NewGuid();
            var client = "jularr-server-" + publicId.ToString("N");
            var nonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var pin = await _plex.CreatePinAsync(client, CancellationToken);
            var expires = DateTime.UtcNow.AddMinutes(5);
            byte[] protectedState;
            var stateBytes = JsonSerializer.SerializeToUtf8Bytes(new PlexServerPinState(address.AbsoluteUri, pin.PrivateKey));
            try
            {
                protectedState = _protector.Protect(stateBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pin.PrivateKey);
                CryptographicOperations.ZeroMemory(stateBytes);
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                // Bind the intended server and the initiating Owner before showing the external grant.
                await ctx.EnableProviderAsync("plex", ct);
                await ctx.CreatePlexFlowAsync(new PlexChallengeCreationData(publicId, SHA256.HashData(Encoding.UTF8.GetBytes(nonce)), protectedState, expires, Auth.AccountId), pin.Id, client, ct);
                return true;
            }, CancellationToken);
            return new StartPlexResultV1(publicId, "https://app.plex.tv/auth#?clientID=" + Uri.EscapeDataString(client) + "&code=" + Uri.EscapeDataString(pin.Code)
                + "&context%5Bdevice%5D%5Bproduct%5D=Jularr", nonce, expires);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/providers/plex/pin/{authChallengePublicId:guid}")]
    public sealed class CompletePlexServerPinV1 : AdminService<CompletePlexParametersV1, NoData>
    {
        private readonly PlexClient _client;
        private readonly PlexServer _plex;
        private readonly IDataProtector _protector;
        private readonly IHttpContextAccessor _http;

        internal CompletePlexServerPinV1(PlexClient client, PlexServer plex, IDataProtectionProvider protection, IHttpContextAccessor http)
        {
            _client = client;
            _plex = plex;
            _protector = protection.CreateProtector(StartPlexServerPinV1.ProtectorPurpose);
            _http = http;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CompletePlexParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(CompletePlexParametersV1 parameters, Type resultType) => new("providers.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var nonce = _http.HttpContext?.Request.Cookies[ServiceApi.AuthenticationNonceCookieName]
                ?? throw new UnauthorizedAccessException("The connection attempt is unavailable.");
            var parameters = new PlexChallengeParameters(Parameters.AuthChallengePublicId, nonce, Auth.AccountId);
            var flow = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadPlexFlowAsync(parameters, ct), CancellationToken);
            PlexServerPinState state;
            try
            {
                state = JsonSerializer.Deserialize<PlexServerPinState>(_protector.Unprotect(flow.ProtectedState))
                    ?? throw new UnauthorizedAccessException("The connection attempt is unavailable.");
            }
            catch (CryptographicException)
            {
                throw new UnauthorizedAccessException("The connection attempt is unavailable.");
            }

            PlexIdentity? identity;
            try
            {
                identity = await _client.ReadIdentityAsync(flow.ExternalPinId, flow.ClientIdentifier, state.PrivateKey, CancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(state.PrivateKey);
            }

            if (identity is null) return new UpdateResult(false);
            var server = await _plex.ReadServerAsync(new PlexServerConnection(new Uri(state.BaseUrl), identity.AccessToken), CancellationToken);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.ConsumePlexFlowAsync(parameters, ct);
                await ctx.SavePlexServerAsync(new PlexServerSaveData(state.BaseUrl, _plex.Protect(identity.AccessToken), server.MachineIdentifier), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }

    internal sealed record PlexServerPinState(string BaseUrl, byte[] PrivateKey);
}
