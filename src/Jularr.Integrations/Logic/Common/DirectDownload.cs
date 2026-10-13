using System.Globalization;
using System.Net;
using Jularr.Integrations.Data.Common;

namespace Jularr.Integrations.Logic.Common
{
    // Bounded HTTPS downloads for direct sources: own-origin credentials, limited redirects, one renewal of expired links.
    internal static class DirectDownload
    {
        private const int MaximumRedirects = 5;
        private static readonly string[] GateMarkers = ["cf-challenge", "challenge-platform", "ddos-guard", "g-recaptcha", "h-captcha", "captcha"];

        public static async Task<HttpResponseMessage> SendAsync(HttpClient http, Uri uri, DirectCredentials? credentials, ISourceGateResolver gates, CancellationToken ct, bool publicNetworkOnly = false)
        {
            // Expired signed links are renewed once from the stable address.
            for (var attempt = 0; ; attempt++)
            {
                var (response, final) = await FollowAsync(http, uri, credentials, gates, ct, publicNetworkOnly);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone && final != uri && attempt == 0)
                {
                    response.Dispose();
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    response.Dispose();
                    throw new DirectDownloadException(DirectDownloadFailure.Unauthorized, "The source refused the request.");
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    response.Dispose();
                    throw new DirectDownloadException(DirectDownloadFailure.NotFound, "The source does not have the file.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    response.Dispose();
                    throw new DirectDownloadException(DirectDownloadFailure.Unavailable, "The source answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                }

                return response;
            }
        }

        public static async Task SaveAsync(HttpResponseMessage response, string destination, long maximumBytes, CancellationToken ct)
        {
            if (response.Content.Headers.ContentLength > maximumBytes)
            {
                throw new DirectDownloadException(DirectDownloadFailure.TooLarge, "The file is too large.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > maximumBytes)
                {
                    throw new DirectDownloadException(DirectDownloadFailure.TooLarge, "The file is too large.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }

        private static async Task<(HttpResponseMessage Response, Uri Final)> FollowAsync(HttpClient http, Uri uri, DirectCredentials? credentials, ISourceGateResolver gates, CancellationToken ct, bool publicNetworkOnly)
        {
            string? cookie = null;
            var resolved = false;
            for (var redirect = 0; ; redirect++)
            {
                if (publicNetworkOnly && !IsPublicFileUri(uri))
                {
                    throw new DirectDownloadException(DirectDownloadFailure.ForbiddenRedirect, "A file download must use a public HTTPS endpoint.");
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (credentials?.Covers(uri) == true)
                {
                    request.Headers.TryAddWithoutValidation("Authorization", credentials.Authorization);
                }

                if (cookie is not null)
                {
                    request.Headers.TryAddWithoutValidation("Cookie", cookie);
                }

                HttpResponseMessage response;
                try
                {
                    response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    throw new DirectDownloadException(DirectDownloadFailure.Unavailable, "The source could not be reached.", exception);
                }

                // Gates go to the resolver once; production resolvers never pass them.
                if (await ReadGateAsync(response, uri, ct) is { } gate)
                {
                    response.Dispose();
                    if (resolved || await gates.ResolveAsync(gate, ct) is not { } granted)
                    {
                        throw new DirectDownloadException(DirectDownloadFailure.Gate, "The source asks for a manual confirmation.");
                    }

                    cookie = granted;
                    resolved = true;
                    continue;
                }

                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    response.Dispose();
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (redirect >= MaximumRedirects || next.Scheme != Uri.UriSchemeHttps && next.Scheme != uri.Scheme)
                    {
                        throw new DirectDownloadException(DirectDownloadFailure.ForbiddenRedirect, "The source redirected too often or to an insecure address.");
                    }

                    // A cookie never follows to another host.
                    if (!string.Equals(next.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
                    {
                        cookie = null;
                    }

                    uri = next;
                    continue;
                }

                return (response, uri);
            }
        }

        // Reject local addresses at input and at every redirect. The transport must also enforce DNS/IP restrictions.
        public static bool IsPublicFileUri(Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.IsLoopback || uri.Port != 443
                || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (IPAddress.TryParse(uri.IdnHost, out var address))
            {
                return IsPublicAddress(address);
            }

            return uri.HostNameType == UriHostNameType.Dns && uri.IdnHost.Contains('.');
        }

        public static bool IsPublicAddress(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                var bytes = address.GetAddressBytes();
                return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
            }

            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return false;
            }

            var octets = address.GetAddressBytes();
            return octets[0] is >= 1 and < 224 && octets[0] != 10 && octets[0] != 127
                && !(octets[0] == 100 && octets[1] is >= 64 and <= 127)
                && !(octets[0] == 169 && octets[1] == 254)
                && !(octets[0] == 172 && octets[1] is >= 16 and <= 31)
                && !(octets[0] == 192 && octets[1] == 168)
                && !(octets[0] == 192 && octets[1] == 0 && (octets[2] == 0 || octets[2] == 2))
                && !(octets[0] == 198 && octets[1] is 18 or 19)
                && !(octets[0] == 198 && octets[1] == 51 && octets[2] == 100)
                && !(octets[0] == 203 && octets[1] == 0 && octets[2] == 113);
        }

        private static async Task<SourceGate?> ReadGateAsync(HttpResponseMessage response, Uri uri, CancellationToken ct)
        {
            // Challenges announce themselves by header or by a recognizable page.
            var status = (int)response.StatusCode;
            if (status is not (401 or 403 or 429 or 503))
            {
                return null;
            }

            var token = response.Headers.TryGetValues("X-Challenge-Token", out var tokens) ? tokens.FirstOrDefault() : null;
            if (response.Headers.TryGetValues("cf-mitigated", out var mitigated) && mitigated.Contains("challenge", StringComparer.OrdinalIgnoreCase) || token is not null)
            {
                return new SourceGate(uri, status, token);
            }

            if (response.Content.Headers.ContentType?.MediaType != "text/html")
            {
                return null;
            }

            var page = await response.Content.ReadAsStringAsync(ct);
            return GateMarkers.Any(marker => page.Contains(marker, StringComparison.OrdinalIgnoreCase)) ? new SourceGate(uri, status, null) : null;
        }
    }

    internal sealed class NoSourceGateResolver : ISourceGateResolver
    {
        public Task<string?> ResolveAsync(SourceGate gate, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
