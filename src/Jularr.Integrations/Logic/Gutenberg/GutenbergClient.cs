using System.Globalization;
using System.Net;
using System.Text.Json;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Gutenberg;
using Jularr.Integrations.Logic.Common;

namespace Jularr.Integrations.Logic.Gutenberg
{
    // Project Gutenberg through a Gutendex catalog; downloads stay on gutenberg.org.
    internal sealed class GutenbergClient(HttpClient http)
    {
        private const int MaximumRedirects = 5;

        // Public-domain EPUB with images; the catalog id is all a download needs.
        public static Uri EpubUri(int bookId) => new("https://www.gutenberg.org/ebooks/" + bookId.ToString(CultureInfo.InvariantCulture) + ".epub3.images");

        public async Task<IReadOnlyList<GutenbergBook>> SearchAsync(Uri catalog, string query, CancellationToken ct)
        {
            using var response = await SendAsync(new Uri(catalog, "books?search=" + Uri.EscapeDataString(query)), ct);
            JsonDocument document;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (JsonException exception)
            {
                throw new GutenbergException("The Gutenberg catalog returned invalid JSON.", exception);
            }

            using (document)
            {
                var books = new List<GutenbergBook>();
                if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                {
                    return books;
                }

                foreach (var result in results.EnumerateArray().Take(32))
                {
                    if (!result.TryGetProperty("id", out var id) || !id.TryGetInt32(out var bookId) || bookId <= 0
                        || !result.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    // Only books Gutenberg offers as EPUB.
                    if (!result.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Object || !formats.TryGetProperty("application/epub+zip", out _))
                    {
                        continue;
                    }

                    var authors = result.TryGetProperty("authors", out var people) && people.ValueKind == JsonValueKind.Array
                        ? people.EnumerateArray().Select(person => person.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? Name(name.GetString()!) : null)
                            .OfType<string>().Take(3).ToArray()
                        : [];
                    var languages = result.TryGetProperty("languages", out var codes) && codes.ValueKind == JsonValueKind.Array
                        ? codes.EnumerateArray().Where(code => code.ValueKind == JsonValueKind.String).Select(code => code.GetString()!).Take(5).ToArray()
                        : [];
                    books.Add(new GutenbergBook(bookId, title.GetString()!.Trim(), authors, languages));
                }

                return books;
            }
        }

        public async Task DownloadAsync(int bookId, string destination, CancellationToken ct, long? maximumBytes = null, Func<long, IDisposable>? reserveWrite = null)
        {
            // Redirects stay on gutenberg.org over HTTPS; the size is bounded.
            using var response = await SendAsync(EpubUri(bookId), ct);
            try
            {
                await DirectDownload.SaveAsync(response, destination, maximumBytes, ct, reserveWrite);
            }
            catch (DirectDownloadException exception) when (exception.Failure != DirectDownloadFailure.TooLarge)
            {
                throw new GutenbergException("The book is too large.", exception);
            }
        }

        private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct)
        {
            for (var redirect = 0; ; redirect++)
            {
                HttpResponseMessage response;
                try
                {
                    response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    throw new GutenbergException("Gutenberg could not be reached.", exception);
                }

                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location;
                    response.Dispose();
                    var next = location is null ? null : location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (next is null || redirect >= MaximumRedirects || next.Scheme != Uri.UriSchemeHttps || !IsAllowedHost(uri, next))
                    {
                        throw new GutenbergException("Gutenberg redirected to a host that is not allowed.");
                    }

                    uri = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    response.Dispose();
                    throw new GutenbergException("Gutenberg answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
                }

                return response;
            }
        }

        // The catalog keeps its own host; downloads stay on gutenberg.org.
        private static bool IsAllowedHost(Uri from, Uri to) =>
            string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase)
            || to.Host.Equals("gutenberg.org", StringComparison.OrdinalIgnoreCase)
            || to.Host.EndsWith(".gutenberg.org", StringComparison.OrdinalIgnoreCase);

        // "Austen, Jane" reads as "Jane Austen".
        private static string? Name(string value)
        {
            var parts = value.Split(',', 2, StringSplitOptions.TrimEntries);
            var name = parts.Length == 2 && parts[1].Length > 0 ? parts[1] + " " + parts[0] : value.Trim();
            return name.Length is > 0 and <= 200 ? name : null;
        }
    }
}
