using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Plex;
using System.Buffers.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Jularr.Integrations.Logic.Plex
{
    public sealed class PlexClient
    {
        private readonly HttpClient _http;

        public PlexClient(HttpClient http)
        {
            _http = http;
        }

        public async Task<PlexPin> CreatePinAsync(string clientIdentifier, CancellationToken cancellationToken)
        {
            // Create device proof.
            var key = new Ed25519PrivateKeyParameters(new SecureRandom());
            var jwk = new
            {
                kty = "OKP",
                crv = "Ed25519",
                x = Base64Url.EncodeToString(key.GeneratePublicKey().GetEncoded()),
                kid = clientIdentifier,
                alg = "EdDSA"
            };

            // Request a Plex PIN.
            using var request = CreateRequest(HttpMethod.Post, new Uri("https://clients.plex.tv/api/v2/pins"), clientIdentifier);
            request.Content = JsonContent.Create(new { strong = true, jwk });
            using var json = await SendAsync(request, cancellationToken);

            // Check provider response.
            var id = json.RootElement.GetProperty("id").GetInt64();
            var code = json.RootElement.GetProperty("code").GetString();
            if (id <= 0 || string.IsNullOrWhiteSpace(code) || code.Length > 128)
            {
                throw new ProviderUnavailableException();
            }

            return new PlexPin(id, code, key.GetEncoded());
        }

        public async Task<PlexIdentity?> ReadIdentityAsync(long externalPinId, string clientIdentifier, byte[] privateKey, CancellationToken cancellationToken)
        {
            // Poll the device grant.
            var deviceJwt = CreateDeviceJwt(privateKey, clientIdentifier);
            var pinUri = new Uri($"https://clients.plex.tv/api/v2/pins/{externalPinId}?deviceJWT={deviceJwt}");
            using var pinRequest = CreateRequest(HttpMethod.Get, pinUri, clientIdentifier);
            using var pin = await SendAsync(pinRequest, cancellationToken);

            if (!pin.RootElement.TryGetProperty("authToken", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tokenElement.GetString()))
            {
                return null;
            }

            // Read the granted identity.
            var token = tokenElement.GetString()!;
            using var userRequest = CreateRequest(HttpMethod.Get, new Uri("https://clients.plex.tv/api/v2/user"), clientIdentifier, token);
            using var user = await SendAsync(userRequest, cancellationToken);

            var root = user.RootElement;
            var id = root.GetProperty("id").ToString();
            var email = root.TryGetProperty("email", out var emailValue) ? emailValue.GetString() ?? "" : "";
            var name = root.TryGetProperty("username", out var nameValue) ? nameValue.GetString() ?? "" : "";
            var confirmed = root.TryGetProperty("confirmed", out var confirmation) && confirmation.ValueKind == JsonValueKind.True;

            return new PlexIdentity(id, email, name, confirmed, token);
        }

        public async Task<bool> HasLibraryAccessAsync(
            string clientIdentifier,
            string accessToken,
            Uri serverUri,
            string serverIdentifier,
            IReadOnlyList<string> libraryKeys,
            CancellationToken cancellationToken)
        {
            // Only trusted HTTPS endpoints.
            if (!serverUri.IsAbsoluteUri || serverUri.Scheme != "https" || serverUri.AbsolutePath != "/"
                || serverUri.UserInfo.Length != 0 || serverUri.Query.Length != 0 || serverUri.Fragment.Length != 0)
            {
                throw new ArgumentException("A configured HTTPS server root is required.", nameof(serverUri));
            }

            // Use the user's grant.
            using var request = CreateRequest(HttpMethod.Get,
                new Uri("https://clients.plex.tv/api/v2/resources?includeHttps=1&includeRelay=0"), clientIdentifier, accessToken);
            using var resources = await SendAsync(request, cancellationToken);

            // Resolve the user's server token.
            var token = "";
            foreach (var resource in resources.RootElement.EnumerateArray())
            {
                if (resource.GetProperty("clientIdentifier").GetString() == serverIdentifier
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
                // Ignore provider-supplied URLs.
                using var serverRequest = CreateRequest(HttpMethod.Get, new Uri(serverUri, "identity"), clientIdentifier, token);
                using var server = await SendAsync(serverRequest, cancellationToken);
                if (server.RootElement.GetProperty("MediaContainer").GetProperty("machineIdentifier").GetString() != serverIdentifier)
                {
                    return false;
                }

                // Read accessible libraries.
                using var sectionsRequest = CreateRequest(HttpMethod.Get, new Uri(serverUri, "library/sections"), clientIdentifier, token);
                using var sections = await SendAsync(sectionsRequest, cancellationToken);
                if (!sections.RootElement.GetProperty("MediaContainer").TryGetProperty("Directory", out var libraries))
                {
                    return false;
                }

                foreach (var library in libraries.EnumerateArray())
                {
                    var key = library.GetProperty("key").ToString();
                    if (libraryKeys.Contains(key, StringComparer.Ordinal))
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
            // Sign a short-lived proof.
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new { alg = "EdDSA", kid = clientIdentifier, typ = "JWT" }));
            var payload = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new
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

            return header + "." + payload + "." + Base64Url.EncodeToString(signer.GenerateSignature());
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

                // Parse bounded HTTP content.
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
