using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Jularr.Integrations.Data.Synology;

namespace Jularr.Integrations.Logic.Synology
{
    // DSM Web API: one session per action; the password travels in the form body only.
    internal sealed class SynologyClient(HttpClient http)
    {
        private static readonly HttpRequestOptionsKey<string?> Pinned = new("Jularr.SynologyPinned");
        private static readonly HttpRequestOptionsKey<SynologyCertificate> Presented = new("Jularr.SynologyPresented");

        // A certificate counts when a public authority vouches for it or the Owner confirmed exactly this one.
        public static HttpMessageHandler CreateHandler() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
            {
                if (certificate is null)
                {
                    return false;
                }

                var presented = new SynologyCertificate(Convert.ToHexString(SHA256.HashData(certificate.RawData)), errors == SslPolicyErrors.None);
                request.Options.Set(Presented, presented);
                return Accepts(request.Options.TryGetValue(Pinned, out var pinned) ? pinned : null, presented);
            }
        };

        public static bool Accepts(string? pinned, SynologyCertificate presented) => presented.IsValid || string.Equals(pinned, presented.Sha256, StringComparison.OrdinalIgnoreCase);

        // The certificate the address presents now; none over plain HTTP.
        public async Task<SynologyCertificate?> ReadCertificateAsync(Uri baseAddress, CancellationToken ct)
        {
            if (baseAddress.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, "webapi/query.cgi"));
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (!request.Options.TryGetValue(Presented, out _))
                {
                    throw new SynologyException("synology_unavailable", exception);
                }
            }

            return request.Options.TryGetValue(Presented, out var presented) ? presented : throw new SynologyException("synology_unavailable");
        }

        public async Task<SynologyStatus> ReadStatusAsync(SynologyAccess access, CancellationToken ct)
        {
            var session = await LoginAsync(access, ct);
            try
            {
                var system = await CallAsync(access, session, new() { ["api"] = "SYNO.Core.System", ["version"] = "1", ["method"] = "info" }, ct);
                var storage = await CallAsync(access, session, new() { ["api"] = "SYNO.Storage.CGI.Storage", ["version"] = "1", ["method"] = "load_info" }, ct);
                return new SynologyStatus(Text(system, "model"), Text(system, "firmware_ver"), Number(system, "sys_temp"),
                    [.. Items(storage, "volumes").Select(volume => new SynologyVolume(Text(volume, "vol_path") ?? Text(volume, "id"), Text(volume, "status"), Bytes(volume, "total"), Bytes(volume, "used")))],
                    [.. Items(storage, "disks").Select(disk => new SynologyDisk(Text(disk, "name") ?? Text(disk, "id"), Text(disk, "model")?.Trim(), Text(disk, "status"), Number(disk, "temp")))]);
            }
            finally
            {
                await LogoutAsync(access, session);
            }
        }

        // A NAS that goes down may drop the answer to its own power command.
        public async Task PowerAsync(SynologyAccess access, bool restart, CancellationToken ct)
        {
            var session = await LoginAsync(access, ct);
            try
            {
                await CallAsync(access, session, new() { ["api"] = "SYNO.Core.System", ["version"] = "1", ["method"] = restart ? "reboot" : "shutdown" }, ct);
            }
            catch (SynologyException exception) when (exception.InnerException is HttpRequestException { HttpRequestError: not (HttpRequestError.ConnectionError
                or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError) } or TaskCanceledException or JsonException)
            {
                // Sent; the answer was lost. Refusals and unreachable NAS still surface.
            }
        }

        private async Task<string> LoginAsync(SynologyAccess access, CancellationToken ct)
        {
            var data = await CallAsync(access, null, new()
            {
                ["api"] = "SYNO.API.Auth",
                ["version"] = "6",
                ["method"] = "login",
                ["account"] = access.Username,
                ["passwd"] = access.Password,
                ["session"] = "Jularr",
                ["format"] = "sid"
            }, ct);
            return Text(data, "sid") ?? throw new SynologyException("synology_unavailable");
        }

        private async Task LogoutAsync(SynologyAccess access, string session)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await CallAsync(access, session, new() { ["api"] = "SYNO.API.Auth", ["version"] = "6", ["method"] = "logout", ["session"] = "Jularr" }, timeout.Token);
            }
            catch (Exception exception) when (exception is SynologyException or OperationCanceledException)
            {
                // DSM ends idle sessions itself.
            }
        }

        private async Task<JsonElement> CallAsync(SynologyAccess access, string? session, Dictionary<string, string> form, CancellationToken ct)
        {
            if (session is not null)
            {
                form["_sid"] = session;
            }

            JsonDocument document;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(access.BaseAddress, "webapi/entry.cgi")) { Content = new FormUrlEncodedContent(form) };
                request.Options.Set(Pinned, access.CertificateSha256);
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new SynologyException("synology_unavailable");
                }

                document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            }
            catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.SecureConnectionError)
            {
                throw new SynologyException("synology_certificate_untrusted", exception);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new SynologyException("synology_unavailable", exception);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
                {
                    return root.TryGetProperty("data", out var data) ? data.Clone() : default;
                }

                // DSM codes: wrong, disabled or expired login; two-step verification; blocked address; no permission.
                var code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("code", out var number) && number.TryGetInt32(out var value) ? value : 0;
                throw new SynologyException(code switch
                {
                    400 or 401 or 402 or 408 or 409 or 410 => "synology_login_failed",
                    403 or 404 or 406 => "synology_two_factor",
                    407 => "synology_blocked",
                    105 => "synology_not_admin",
                    _ => "synology_unavailable"
                });
            }
        }

        private static IEnumerable<JsonElement> Items(JsonElement data, string name) =>
            data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];

        private static string? Text(JsonElement data, string name) =>
            data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static int? Number(JsonElement data, string name) =>
            data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

        // Volume sizes arrive as decimal strings below "size".
        private static long? Bytes(JsonElement volume, string name) =>
            volume.ValueKind == JsonValueKind.Object && volume.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Object
                && size.TryGetProperty(name, out var value) && long.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var bytes) ? bytes : null;
    }
}
