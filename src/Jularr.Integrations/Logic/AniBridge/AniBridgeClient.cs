using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.AniBridge;

namespace Jularr.Integrations.Logic.AniBridge
{
    // The AniBridge mappings (anibridge/anibridge-mappings, MIT): a conditional download of the release's minified file and its AniList-to-TheTVDB part.
    // The file maps "anilist:<id>" to "tvdb_show:<id>:s<season>" with ranges "AniList episodes": "TheTVDB episodes" ("1-12": "13-24", open ends "13-",
    // several target segments "1-6,8-13" and a trailing episode ratio "|2"). Mappings are one-way; only AniList's side is read.
    internal sealed partial class AniBridgeClient(HttpClient http)
    {
        private const string FileName = "mappings.min.json";
        private const int MaximumBytes = 128 * 1024 * 1024;

        [GeneratedRegex(@"\Aanilist:([1-9][0-9]{0,8})\z")]
        private static partial Regex AniListKey();

        [GeneratedRegex(@"\Atvdb_show:([1-9][0-9]{0,8}):s([0-9]{1,4})\z")]
        private static partial Regex TvdbSeasonKey();

        [GeneratedRegex(@"\A([1-9][0-9]{0,5})(-([1-9][0-9]{0,5})?)?\z")]
        private static partial Regex Range();

        // Null while the release still has this ETag.
        public async Task<AniBridgeRelease?> ReadAsync(string? etag, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, FileName);
            if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag))
            {
                request.Headers.IfNoneMatch.Add(tag);
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumBytes)
            {
                throw new InvalidDataException("The anime mappings are too large.");
            }

            // The file is read whole, within its limit, before it is parsed.
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var buffered = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (buffered.Length + read > MaximumBytes)
                {
                    throw new InvalidDataException("The anime mappings are too large.");
                }

                buffered.Write(buffer, 0, read);
            }

            buffered.Position = 0;
            return Parse(buffered, response.Headers.ETag?.ToString());
        }

        // A release of schema 3 with at least one AniList entry in a TheTVDB season; anything else is refused.
        internal static AniBridgeRelease Parse(Stream json, string? etag)
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("$meta", out var meta) || meta.ValueKind != JsonValueKind.Object
                || Text(meta, "schema_version") is not { Length: <= 32 } schema || !schema.StartsWith("3.", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The anime mappings have an unknown schema.");
            }

            DateTimeOffset? generatedAt = Text(meta, "generated_on") is { } generated
                && DateTimeOffset.TryParse(generated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
            var mappings = new List<AniBridgeMapping>();
            foreach (var source in root.EnumerateObject())
            {
                var aniList = AniListKey().Match(source.Name);
                if (!aniList.Success || source.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var aniListId = int.Parse(aniList.Groups[1].Value, CultureInfo.InvariantCulture);
                foreach (var target in source.Value.EnumerateObject())
                {
                    var season = TvdbSeasonKey().Match(target.Name);
                    if (season.Success && target.Value.ValueKind == JsonValueKind.Object
                        && Mapping(aniListId, int.Parse(season.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(season.Groups[2].Value, CultureInfo.InvariantCulture), target.Value) is { } mapping)
                    {
                        mappings.Add(mapping);
                    }
                }
            }

            return mappings.Count > 0 ? new AniBridgeRelease(etag, schema, generatedAt, mappings) : throw new InvalidDataException("The anime mappings name no TheTVDB season.");
        }

        // Where an entry's earliest episode lands in the season and how far its episodes reach; without ranges the entry is the whole season.
        // A ratio changes how episodes split, not where they start. A range the format does not allow leaves the entry's season out.
        private static AniBridgeMapping? Mapping(int aniListId, int tvdbId, int seasonNumber, JsonElement ranges)
        {
            (int Source, int First)? start = null;
            int? last = 0;
            foreach (var range in ranges.EnumerateObject())
            {
                if (range.Value.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var segments = range.Value.GetString()!.Split('|')[0].Split(',');
                var source = Range().Match(range.Name);
                var first = Range().Match(segments[0]);
                var end = Range().Match(segments[^1]);
                if (!source.Success || !first.Success || !end.Success)
                {
                    return null;
                }

                var sourceFirst = int.Parse(source.Groups[1].Value, CultureInfo.InvariantCulture);
                if (start is null || sourceFirst < start.Value.Source)
                {
                    start = (sourceFirst, int.Parse(first.Groups[1].Value, CultureInfo.InvariantCulture));
                }

                // "x" ends at x, "x-y" at y, "x-" runs on.
                int? reach = !end.Groups[2].Success ? int.Parse(end.Groups[1].Value, CultureInfo.InvariantCulture)
                    : end.Groups[3].Success ? int.Parse(end.Groups[3].Value, CultureInfo.InvariantCulture) : null;
                last = last is null || reach is null ? null : Math.Max(last.Value, reach.Value);
            }

            if (start is not { } from)
            {
                return new AniBridgeMapping(aniListId, tvdbId, seasonNumber, 1, 1, null);
            }

            return last is null || last >= from.First ? new AniBridgeMapping(aniListId, tvdbId, seasonNumber, from.Source, from.First, last) : null;
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
