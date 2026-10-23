using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Jularr.Integrations.Data.Subtitles;

namespace Jularr.Integrations.Logic.Subtitles
{
    // SubDL API: search with the API key; every subtitle is a ZIP from its download host.
    internal sealed class SubDlClient(HttpClient http)
    {
        private const long MaximumBytes = 10L * 1024 * 1024;
        private static readonly Uri DownloadHost = new("https://dl.subdl.com/");

        public async Task<IReadOnlyList<SubtitleOffer>> SearchAsync(SubtitleAccess access, SubtitleQuery query, CancellationToken ct)
        {
            var parameters = new List<string>
            {
                "api_key=" + Uri.EscapeDataString(access.ApiKey),
                "languages=" + string.Join(',', query.Languages.Select(language => language.ToUpperInvariant())),
                "subs_per_page=30",
                "type=" + (query.IsEpisode ? "tv" : "movie")
            };
            parameters.Add(query.ImdbId is not null ? "imdb_id=" + Uri.EscapeDataString(query.ImdbId) : "tmdb_id=" + query.TmdbId);
            if (query.IsEpisode)
            {
                parameters.Add("season_number=" + query.Season?.ToString(CultureInfo.InvariantCulture));
                parameters.Add("episode_number=" + query.Episode?.ToString(CultureInfo.InvariantCulture));
            }

            using var document = await ReadJsonAsync(new Uri(new Uri(access.BaseUrl.TrimEnd('/') + "/"), "subtitles?" + string.Join('&', parameters)), ct);
            var root = document.RootElement;
            if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.False)
            {
                // An unknown title is no failure; a rejected key is.
                return Text(root, "error") is { } error && error.Contains("api", StringComparison.OrdinalIgnoreCase) ? throw new SubtitleException("acquisition_authentication_failed") : [];
            }

            var offers = new List<SubtitleOffer>();
            if (root.TryGetProperty("subtitles", out var subtitles) && subtitles.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in subtitles.EnumerateArray())
                {
                    if (Text(item, "url") is { } url && url.StartsWith('/') && Text(item, "language") is { } language)
                    {
                        // "BR_PT" is Brazilian Portuguese.
                        offers.Add(new SubtitleOffer(url, language.Split('_')[^1].ToLowerInvariant(), Text(item, "release_name"), item.TryGetProperty("hi", out var hi) && hi.ValueKind == JsonValueKind.True,
                            false, 0));
                    }
                }
            }

            return offers;
        }

        // The SubRip file of the archive; a season pack gives the asked episode or nothing.
        public async Task<byte[]> DownloadAsync(SubtitleOffer offer, SubtitleQuery query, CancellationToken ct)
        {
            byte[] archive;
            try
            {
                using var response = await http.GetAsync(new Uri(DownloadHost, offer.Handle.TrimStart('/')), HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes)
                {
                    throw new SubtitleException("acquisition_unavailable");
                }

                archive = await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new SubtitleException("acquisition_unavailable", exception);
            }

            try
            {
                using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
                var entries = zip.Entries.Where(item => item.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) && item.Length is > 0 and <= MaximumBytes).ToArray();
                var episode = query.IsEpisode ? string.Create(CultureInfo.InvariantCulture, $"S{query.Season:00}E{query.Episode:00}") : null;
                var entry = (entries.Length == 1 ? entries[0] : entries.FirstOrDefault(item => episode is not null && item.Name.Contains(episode, StringComparison.OrdinalIgnoreCase)))
                    ?? throw new SubtitleException("acquisition_invalid_response");
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy, ct);
                return copy.ToArray();
            }
            catch (InvalidDataException exception)
            {
                throw new SubtitleException("acquisition_invalid_response", exception);
            }
        }

        // A search for a well-known film proves the key.
        public Task TestAsync(SubtitleAccess access, CancellationToken ct) => SearchAsync(access, new SubtitleQuery(false, "tt0133093", null, null, null, ["en"]), ct);

        private async Task<JsonDocument> ReadJsonAsync(Uri address, CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(address, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new SubtitleException("acquisition_unavailable", exception);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new SubtitleException("acquisition_authentication_failed");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new SubtitleException("acquisition_rate_limited");
                }

                try
                {
                    return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                }
                catch (JsonException exception)
                {
                    throw new SubtitleException(response.IsSuccessStatusCode ? "acquisition_invalid_response" : "acquisition_unavailable", exception);
                }
            }
        }

        private static string? Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
    }
}
