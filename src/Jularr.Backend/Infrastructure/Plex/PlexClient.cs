using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Authentication;
using Jularr.Backend.Service.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Jularr.Backend.Infrastructure.Plex
{
    public sealed class PlexClient
    {
        private readonly HttpClient _http;
        private readonly AuthenticationOptions _options;

        public PlexClient(HttpClient http, IOptions<AuthenticationOptions> options)
        {
            _http = http;
            _options = options.Value;
        }

        public async Task<PlexPinData> CreatePinAsync(string clientIdentifier, CancellationToken cancellationToken)
        {
            var key = new Ed25519PrivateKeyParameters(new SecureRandom());
            var jwk = new
            {
                kty = "OKP",
                crv = "Ed25519",
                x = WebEncoders.Base64UrlEncode(key.GeneratePublicKey().GetEncoded()),
                kid = clientIdentifier,
                alg = "EdDSA"
            };

            using var request = CreateRequest(HttpMethod.Post, new Uri("https://clients.plex.tv/api/v2/pins"), clientIdentifier);
            request.Content = JsonContent.Create(new { strong = true, jwk });
            using var json = await SendAsync(request, cancellationToken);
            var id = json.RootElement.GetProperty("id").GetInt64();
            var code = json.RootElement.GetProperty("code").GetString();
            if (id <= 0 || string.IsNullOrWhiteSpace(code) || code.Length > 128)
            {
                throw new ProviderUnavailableException();
            }

            return new PlexPinData(id, code, key.GetEncoded());
        }

        public async Task<PlexIdentityData?> ReadIdentityAsync(
            PlexFlowData flow,
            byte[] privateKey,
            CancellationToken cancellationToken)
        {
            var deviceJwt = CreateDeviceJwt(privateKey, flow.ClientIdentifier);
            var pinUri = new Uri($"https://clients.plex.tv/api/v2/pins/{flow.ExternalPinId}?deviceJWT={deviceJwt}");
            using var pinRequest = CreateRequest(HttpMethod.Get, pinUri, flow.ClientIdentifier);
            using var pin = await SendAsync(pinRequest, cancellationToken);

            if (!pin.RootElement.TryGetProperty("authToken", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tokenElement.GetString()))
            {
                return null;
            }

            var token = tokenElement.GetString()!;
            using var userRequest = CreateRequest(HttpMethod.Get, new Uri("https://clients.plex.tv/api/v2/user"), flow.ClientIdentifier, token);
            using var user = await SendAsync(userRequest, cancellationToken);
            var root = user.RootElement;
            var id = root.GetProperty("id").ToString();
            var email = root.TryGetProperty("email", out var emailValue) ? emailValue.GetString() ?? "" : "";
            var name = root.TryGetProperty("username", out var nameValue) ? nameValue.GetString() ?? "" : "";
            var confirmed = root.TryGetProperty("confirmed", out var confirmation) && confirmation.ValueKind == JsonValueKind.True;

            var identity = new PlexIdentityData(id, email, name, confirmed, token);
            identity.Validate();
            return identity;
        }

        public async Task<bool> HasRegistrationLibraryAccessAsync(
            PlexFlowData flow,
            PlexIdentityData identity,
            CancellationToken cancellationToken)
        {
            if (!_options.PlexRegistrationEnabled)
            {
                return false;
            }

            // Use the registering user's grant, never a server owner's broad token.
            using var request = CreateRequest(HttpMethod.Get,
                new Uri("https://clients.plex.tv/api/v2/resources?includeHttps=1&includeRelay=0"), flow.ClientIdentifier, identity.AccessToken);
            using var resources = await SendAsync(request, cancellationToken);
            var token = "";
            foreach (var resource in resources.RootElement.EnumerateArray())
            {
                if (resource.GetProperty("clientIdentifier").GetString() == _options.PlexServerIdentifier
                    && resource.TryGetProperty("provides", out var provides) && provides.GetString()?.Split(',').Contains("server") == true)
                {
                    token = resource.GetProperty("accessToken").GetString() ?? "";
                    break;
                }
            }

            if (token.Length == 0)
            {
                return false;
            }

            try
            {
                // Only the configured HTTPS server is contacted; resource-supplied URLs are ignored.
                using var serverRequest = CreateRequest(HttpMethod.Get, new Uri(_options.PlexServerUri!, "identity"), flow.ClientIdentifier, token);
                using var server = await SendAsync(serverRequest, cancellationToken);
                if (server.RootElement.GetProperty("MediaContainer").GetProperty("machineIdentifier").GetString() != _options.PlexServerIdentifier)
                {
                    return false;
                }

                using var sectionsRequest = CreateRequest(HttpMethod.Get, new Uri(_options.PlexServerUri!, "library/sections"), flow.ClientIdentifier, token);
                using var sections = await SendAsync(sectionsRequest, cancellationToken);
                if (!sections.RootElement.GetProperty("MediaContainer").TryGetProperty("Directory", out var libraries))
                {
                    return false;
                }

                foreach (var library in libraries.EnumerateArray())
                {
                    var key = library.GetProperty("key").ToString();
                    if (_options.PlexRegistrationLibraryKeys.Contains(key, StringComparer.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string CreateDeviceJwt(byte[] privateKey, string clientIdentifier)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "EdDSA", kid = clientIdentifier, typ = "JWT" }));
            var payload = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                aud = "plex.tv",
                iss = clientIdentifier,
                scope = "username,email,friendly_name",
                iat = now,
                exp = now + 300
            }));
            var message = Encoding.ASCII.GetBytes(header + "." + payload);

            var signer = new Ed25519Signer();
            signer.Init(true, new Ed25519PrivateKeyParameters(privateKey));
            signer.BlockUpdate(message, 0, message.Length);

            return header + "." + payload + "." + WebEncoders.Base64UrlEncode(signer.GenerateSignature());
        }

        private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string clientIdentifier, string? token = null)
        {
            var request = new HttpRequestMessage(method, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("X-Plex-Client-Identifier", clientIdentifier);
            request.Headers.Add("X-Plex-Product", "Jularr");
            if (token is not null)
            {
                request.Headers.Add("X-Plex-Token", token);
            }

            return request;
        }

        private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _http.SendAsync(request, cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new UnauthorizedAccessException("The Plex credential is unavailable.");
                }

                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            catch (HttpRequestException)
            {
                throw new ProviderUnavailableException();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProviderUnavailableException();
            }
            catch (JsonException)
            {
                throw new ProviderUnavailableException();
            }
        }
    }
}
