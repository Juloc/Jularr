using System.Globalization;
using System.Net;
using Jularr.Integrations.Data.Browser;
using Jularr.Integrations.Data.Common;

namespace Jularr.Integrations.Logic.Common
{
    // Bounded HTTPS downloads for direct sources: own-origin credentials, limited redirects, one renewal of expired links.
    internal static class DirectDownload
    {
        private const int MaximumRedirects = 5;
        private const long MaximumPageBytes = 4L * 1024 * 1024;
        private static readonly string[] GateMarkers = ["cf-challenge", "challenge-platform", "ddos-guard", "ddg-captcha", "g-recaptcha", "h-captcha"];

        // A page behind a browser check is read by a real browser; without one the check stands and a person takes over.
        public static async Task<string> ReadPageAsync(HttpClient http, Uri uri, ISourceGateResolver gates, ISourceBrowser browser, CancellationToken ct)
        {
            try
            {
                using var response = await SendAsync(http, uri, null, gates, ct);
                var page = await ReadPageContentAsync(response, ct);
                if (response.Content.Headers.ContentType?.MediaType?.Equals("text/html", StringComparison.OrdinalIgnoreCase) == true
                    && IsGatePage(page))
                {
                    throw new DirectDownloadException(DirectDownloadFailure.Gate, "The source returned a browser check instead of its page.");
                }

                return page;
            }
            catch (DirectDownloadException exception) when (exception.Failure == DirectDownloadFailure.Gate)
            {
                var page = await browser.ReadPageAsync(uri, ct);
                if (page is not null)
                {
                    return page;
                }

                throw;
            }
        }

        // A file behind a browser check is fetched by a real browser; the storage rules stay the caller's.
        public static async Task SaveFileAsync(HttpClient http, Uri uri, string destination, ISourceGateResolver gates, ISourceBrowser browser, long? maximumBytes,
            Func<long, IDisposable>? reserveWrite, CancellationToken ct)
        {
            try
            {
                using var response = await SendAsync(http, uri, null, gates, ct);
                await SaveAsync(response, destination, maximumBytes, ct, reserveWrite);
            }
            catch (DirectDownloadException exception) when (exception.Failure == DirectDownloadFailure.Gate)
            {
                await browser.SaveFileAsync(uri, destination, maximumBytes, reserveWrite, ct);
            }
        }

        public static async Task<HttpResponseMessage> SendAsync(HttpClient http, Uri uri, DirectCredentials? credentials, ISourceGateResolver gates, CancellationToken ct)
        {
            // Expired signed links are renewed once from the stable address.
            for (var attempt = 0; ; attempt++)
            {
                var (response, final) = await FollowAsync(http, uri, credentials, gates, ct);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone && final != uri && attempt == 0)
                {
                    response.Dispose();
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    response.Dispose();
                    throw new DirectDownloadException(DirectDownloadFailure.RateLimited, "The source limits requests right now.");
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

        public static async Task SaveAsync(HttpResponseMessage response, string destination, long? maximumBytes, CancellationToken ct, Func<long, IDisposable>? reserveWrite = null)
        {
            if (maximumBytes is null && reserveWrite is null)
            {
                throw new InvalidOperationException("An unlimited download requires a storage reservation before every write.");
            }

            if (maximumBytes is { } limit && response.Content.Headers.ContentLength > limit)
            {
                throw new DirectDownloadException(DirectDownloadFailure.TooLarge, "The file is too large.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await CopyAsync(source, destination, maximumBytes, reserveWrite, ct);
        }

        // The file is written in pieces; the limit and the reservation are the storage's rules.
        public static async Task CopyAsync(Stream source, string destination, long? maximumBytes, Func<long, IDisposable>? reserveWrite, CancellationToken ct)
        {
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                if (read > long.MaxValue - total || maximumBytes is { } maximum && read > maximum - total)
                {
                    throw new DirectDownloadException(DirectDownloadFailure.TooLarge, "The file is too large.");
                }

                using var reservation = reserveWrite?.Invoke(read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
            }
        }

        private static async Task<(HttpResponseMessage Response, Uri Final)> FollowAsync(HttpClient http, Uri uri, DirectCredentials? credentials, ISourceGateResolver gates, CancellationToken ct)
        {
            string? cookie = null;
            var resolved = false;
            for (var redirect = 0; ; redirect++)
            {
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
                    throw new DirectDownloadException(exception is TaskCanceledException ? DirectDownloadFailure.TimedOut : DirectDownloadFailure.Unavailable,
                        "The source could not be reached.", exception);
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

        public static async Task<string> ReadPageContentAsync(HttpResponseMessage response, CancellationToken ct)
        {
            if (response.Content.Headers.ContentLength > MaximumPageBytes)
            {
                throw new DirectDownloadException(DirectDownloadFailure.TooLarge, "The source page is too large.");
            }

            try
            {
                await response.Content.LoadIntoBufferAsync(MaximumPageBytes, ct);
            }
            catch (HttpRequestException exception)
            {
                throw new DirectDownloadException(DirectDownloadFailure.TooLarge, "The source page is too large.", exception);
            }

            return await response.Content.ReadAsStringAsync(ct);
        }

        private static bool IsGatePage(string page) =>
            GateMarkers.Any(marker => page.Contains(marker, StringComparison.OrdinalIgnoreCase))
            || page.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase)
            || page.Contains("<title>DDoS-Guard", StringComparison.OrdinalIgnoreCase);

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

            var page = await ReadPageContentAsync(response, ct);
            return IsGatePage(page) ? new SourceGate(uri, status, null) : null;
        }
    }

    internal sealed class NoSourceGateResolver : ISourceGateResolver
    {
        public Task<string?> ResolveAsync(SourceGate gate, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
