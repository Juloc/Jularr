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

        public static async Task<HttpResponseMessage> SendAsync(HttpClient http, Uri uri, DirectCredentials? credentials, CancellationToken ct)
        {
            // Expired signed links are renewed once from the stable address.
            for (var attempt = 0; ; attempt++)
            {
                var (response, final) = await FollowAsync(http, uri, credentials, ct);
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

        private static async Task<(HttpResponseMessage Response, Uri Final)> FollowAsync(HttpClient http, Uri uri, DirectCredentials? credentials, CancellationToken ct)
        {
            for (var redirect = 0; ; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (credentials?.Covers(uri) == true)
                {
                    request.Headers.TryAddWithoutValidation("Authorization", credentials.Authorization);
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

                // A check is never passed; a person confirms on the source's site.
                if (await IsGateAsync(response, ct))
                {
                    response.Dispose();
                    throw new DirectDownloadException(DirectDownloadFailure.Gate, "The source asks for a manual confirmation.");
                }

                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    response.Dispose();
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (redirect >= MaximumRedirects || next.Scheme != Uri.UriSchemeHttps && next.Scheme != uri.Scheme)
                    {
                        throw new DirectDownloadException(DirectDownloadFailure.ForbiddenRedirect, "The source redirected too often or to an insecure address.");
                    }

                    uri = next;
                    continue;
                }

                return (response, uri);
            }
        }

        private static async Task<bool> IsGateAsync(HttpResponseMessage response, CancellationToken ct)
        {
            // Checks announce themselves by header or by a recognizable page.
            if ((int)response.StatusCode is not (401 or 403 or 429 or 503))
            {
                return false;
            }

            if (response.Headers.TryGetValues("cf-mitigated", out var mitigated) && mitigated.Contains("challenge", StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            if (response.Content.Headers.ContentType?.MediaType != "text/html")
            {
                return false;
            }

            var page = await response.Content.ReadAsStringAsync(ct);
            return GateMarkers.Any(marker => page.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }
    }
}
