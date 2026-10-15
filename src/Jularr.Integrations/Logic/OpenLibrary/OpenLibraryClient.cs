using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.OpenLibrary;

namespace Jularr.Integrations.Logic.OpenLibrary
{
    // Open Library: keyless book metadata and covers.
    internal sealed partial class OpenLibraryClient(HttpClient http)
    {
        private const string Fields = "key,title,author_name,first_publish_year,cover_i,isbn,language";

        public static string? CoverUrl(long? coverId) => coverId is > 0 ? "https://covers.openlibrary.org/b/id/" + coverId.Value.ToString(CultureInfo.InvariantCulture) + "-L.jpg" : null;

        public async Task<IReadOnlyList<OpenLibraryResult>> SearchAsync(string query, int limit, CancellationToken ct)
        {
            using var document = await GetAsync("search.json?q=" + Uri.EscapeDataString(query) + "&fields=" + Fields + "&limit=" + Math.Clamp(limit, 1, 50).ToString(CultureInfo.InvariantCulture), ct);
            return ReadResults(document.RootElement, "docs");
        }

        public async Task<IReadOnlyList<OpenLibraryResult>> ReadTrendingAsync(CancellationToken ct)
        {
            using var document = await GetAsync("trending/weekly.json?limit=20", ct);
            return ReadResults(document.RootElement, "works");
        }

        public async Task<OpenLibraryWork> ReadWorkAsync(string workId, CancellationToken ct)
        {
            if (!WorkId().IsMatch(workId))
            {
                throw new OpenLibraryException(OpenLibraryFailure.NotFound, "Invalid Open Library work id.");
            }

            using var document = await GetAsync("works/" + workId + ".json", ct);
            var root = document.RootElement;
            var title = ReadString(root, "title") ?? throw new OpenLibraryException(OpenLibraryFailure.InvalidResponse, "Open Library returned a work without a title.");
            var description = root.TryGetProperty("description", out var text)
                ? text.ValueKind == JsonValueKind.String ? text.GetString() : text.ValueKind == JsonValueKind.Object ? ReadString(text, "value") : null
                : null;
            var cover = root.TryGetProperty("covers", out var covers) && covers.ValueKind == JsonValueKind.Array
                ? covers.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id) && id > 0).Select(value => value.GetInt64()).FirstOrDefault()
                : 0;

            // Author names need their own records; three are enough for identity.
            var authors = new List<ProviderPerson>();
            if (root.TryGetProperty("authors", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray().Take(3))
                {
                    var key = entry.TryGetProperty("author", out var author) ? ReadString(author, "key") : null;
                    if (key is not null && AuthorKey().IsMatch(key))
                    {
                        using var record = await GetAsync(key.TrimStart('/') + ".json", ct);
                        if (ReadString(record.RootElement, "name") is { } name)
                        {
                            authors.Add(new ProviderPerson(key["/authors/".Length..], name, ProviderPerson.Author));
                        }
                    }
                }
            }

            var year = ReadString(root, "first_publish_date") is { } date && YearText().Match(date) is { Success: true } match ? int.Parse(match.Value, CultureInfo.InvariantCulture) : (int?)null;
            return new OpenLibraryWork(workId, title, [.. authors.Select(author => author.Name)], year, description, cover > 0 ? cover : null) { People = authors };
        }

        // The work's editions; one page is enough to know its languages and formats.
        public async Task<IReadOnlyList<OpenLibraryEdition>> ReadEditionsAsync(string workId, CancellationToken ct)
        {
            if (!WorkId().IsMatch(workId))
            {
                throw new OpenLibraryException(OpenLibraryFailure.NotFound, "Invalid Open Library work id.");
            }

            using var document = await GetAsync("works/" + workId + "/editions.json?limit=100", ct);
            if (!document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var editions = new List<OpenLibraryEdition>();
            foreach (var entry in entries.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object))
            {
                var id = ReadString(entry, "key")?.Split('/').Last();
                if (id is null || !EditionId().IsMatch(id))
                {
                    continue;
                }

                var language = entry.TryGetProperty("languages", out var languages) && languages.ValueKind == JsonValueKind.Array
                    ? languages.EnumerateArray().Select(item => ReadString(item, "key")?.Split('/').Last()).FirstOrDefault(code => code is { Length: 3 } && code.All(char.IsAsciiLetterLower))
                    : null;
                var isbns = ReadArray(entry, "isbn_13").Concat(ReadArray(entry, "isbn_10")).Select(isbn => isbn.Replace("-", "", StringComparison.Ordinal))
                    .Where(isbn => isbn.Length is 10 or 13 && isbn[..^1].All(char.IsAsciiDigit)).Distinct(StringComparer.Ordinal).Take(10).ToArray();
                var year = ReadString(entry, "publish_date") is { } date && YearText().Match(date) is { Success: true } match ? int.Parse(match.Value, CultureInfo.InvariantCulture) : (int?)null;
                var translators = entry.TryGetProperty("contributors", out var contributors) && contributors.ValueKind == JsonValueKind.Array
                    ? contributors.EnumerateArray()
                        .Where(contributor => ReadString(contributor, "role")?.Contains("Translat", StringComparison.OrdinalIgnoreCase) == true)
                        .Select(contributor => ReadString(contributor, "name")).OfType<string>().Take(5)
                        .Select(name => new ProviderPerson(null, name, ProviderPerson.Translator)).ToArray()
                    : [];
                editions.Add(new OpenLibraryEdition(id, ReadString(entry, "title"), language, ReadString(entry, "physical_format"), isbns, ReadArray(entry, "publishers").FirstOrDefault(), year)
                {
                    People = translators
                });
            }

            return editions;
        }

        private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(new Uri(path, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new OpenLibraryException(OpenLibraryFailure.Unavailable, "Open Library could not be reached.", exception);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new OpenLibraryException(OpenLibraryFailure.NotFound, "Open Library does not know this work.");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new OpenLibraryException(OpenLibraryFailure.RateLimited, "Open Library limits requests.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new OpenLibraryException(OpenLibraryFailure.Unavailable, "Open Library failed.");
                }

                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                }
                catch (JsonException exception)
                {
                    throw new OpenLibraryException(OpenLibraryFailure.InvalidResponse, "Open Library returned invalid JSON.", exception);
                }
            }
        }

        private static IReadOnlyList<OpenLibraryResult> ReadResults(JsonElement root, string name)
        {
            var results = new List<OpenLibraryResult>();
            if (!root.TryGetProperty(name, out var docs) || docs.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var doc in docs.EnumerateArray().Where(doc => doc.ValueKind == JsonValueKind.Object))
            {
                var key = ReadString(doc, "key");
                var id = key is null ? null : key.Split('/').Last();
                if (id is null || !WorkId().IsMatch(id) || ReadString(doc, "title") is not { } title)
                {
                    continue;
                }

                var isbn = ReadArray(doc, "isbn").FirstOrDefault(value => value.Length == 13 && value.All(char.IsAsciiDigit)) ?? ReadArray(doc, "isbn").FirstOrDefault(value => value.Length == 10);
                var year = doc.TryGetProperty("first_publish_year", out var value) && value.TryGetInt32(out var number) ? number : (int?)null;
                var cover = doc.TryGetProperty("cover_i", out var image) && image.TryGetInt64(out var coverId) && coverId > 0 ? coverId : (long?)null;
                results.Add(new OpenLibraryResult(id, title, ReadArray(doc, "author_name").Take(3).ToArray(), year, cover, isbn, ReadArray(doc, "language").Take(5).ToArray()));
            }

            return results;
        }

        private static string? ReadString(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;

        private static string[] ReadArray(JsonElement element, string name) =>
            element.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!.Trim()).Where(value => value.Length > 0).ToArray()
                : [];

        [GeneratedRegex("^OL[0-9]{1,12}W$")]
        private static partial Regex WorkId();

        [GeneratedRegex("^OL[0-9]{1,12}M$")]
        private static partial Regex EditionId();

        [GeneratedRegex("^/authors/OL[0-9]{1,12}A$")]
        private static partial Regex AuthorKey();

        [GeneratedRegex("(?:1[5-9]|20)[0-9]{2}")]
        private static partial Regex YearText();
    }
}
