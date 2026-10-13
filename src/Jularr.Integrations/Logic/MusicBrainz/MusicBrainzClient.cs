using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.MusicBrainz;

namespace Jularr.Integrations.Logic.MusicBrainz
{
    // MusicBrainz: keyless album metadata; the service allows one request per second per application.
    internal sealed partial class MusicBrainzClient(HttpClient http)
    {
        private static readonly Uri Api = new("https://musicbrainz.org/ws/2/");
        private static readonly SemaphoreSlim Pace = new(1, 1);
        private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(1100);
        private static DateTimeOffset _last = DateTimeOffset.MinValue;

        [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
        private static partial Regex Mbid();

        public static string CoverUrl(string releaseGroupId) => "https://coverartarchive.org/release-group/" + releaseGroupId + "/front-500";

        public async Task<IReadOnlyList<MusicBrainzAlbum>> SearchAsync(string query, CancellationToken ct)
        {
            using var document = await GetAsync("release-group?fmt=json&limit=20&query=" + Uri.EscapeDataString(query), ct);
            return document.RootElement.TryGetProperty("release-groups", out var groups) && groups.ValueKind == JsonValueKind.Array
                ? [.. groups.EnumerateArray().Select(ReadAlbum).OfType<MusicBrainzAlbum>()]
                : [];
        }

        public async Task<MusicBrainzAlbumDetails> ReadAsync(string releaseGroupId, CancellationToken ct)
        {
            if (!Mbid().IsMatch(releaseGroupId))
            {
                throw new MusicBrainzException(MusicBrainzFailure.NotFound, "Invalid MusicBrainz id.");
            }

            using var group = await GetAsync("release-group/" + releaseGroupId + "?fmt=json&inc=artist-credits", ct);
            var album = ReadAlbum(group.RootElement) ?? throw new MusicBrainzException(MusicBrainzFailure.InvalidResponse, "MusicBrainz returned an album without a title.");

            // The earliest official edition names the tracks.
            using var releases = await GetAsync("release?fmt=json&limit=25&inc=recordings+media&status=official&release-group=" + releaseGroupId, ct);
            var edition = releases.RootElement.TryGetProperty("releases", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().OrderBy(release => Text(release, "date") ?? "9999").FirstOrDefault()
                : default;
            var tracks = new List<MusicBrainzTrack>();
            if (edition.ValueKind == JsonValueKind.Object && edition.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
            {
                foreach (var medium in media.EnumerateArray().OrderBy(medium => medium.TryGetProperty("position", out var position) && position.TryGetInt32(out var value) ? value : 0))
                {
                    if (!medium.TryGetProperty("tracks", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var track in entries.EnumerateArray().Take(500 - tracks.Count))
                    {
                        tracks.Add(new MusicBrainzTrack(tracks.Count + 1, Limit(Text(track, "title") ?? "Track " + (tracks.Count + 1).ToString(CultureInfo.InvariantCulture), 500)));
                    }
                }
            }

            return new MusicBrainzAlbumDetails(album, tracks);
        }

        private static MusicBrainzAlbum? ReadAlbum(JsonElement group)
        {
            if (group.ValueKind != JsonValueKind.Object || Text(group, "id") is not { } id || !Mbid().IsMatch(id) || Text(group, "title") is not { } title)
            {
                return null;
            }

            // Artist credits join their names with the given phrases.
            var artist = group.TryGetProperty("artist-credit", out var credits) && credits.ValueKind == JsonValueKind.Array
                ? string.Concat(credits.EnumerateArray().Select(credit => (Text(credit, "name") ?? "") + (credit.TryGetProperty("joinphrase", out var join) && join.ValueKind == JsonValueKind.String ? join.GetString() : "")))
                : "";
            var date = Text(group, "first-release-date");
            var year = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : (int?)null;
            return new MusicBrainzAlbum(id, Limit(title, 500), Limit(artist.Trim(), 500), year, Text(group, "primary-type"));
        }

        private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
        {
            // One request at a time with the service's spacing.
            await Pace.WaitAsync(ct);
            HttpResponseMessage response;
            try
            {
                var wait = _last + Spacing - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                }

                response = await http.GetAsync(new Uri(Api, path), HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new MusicBrainzException(MusicBrainzFailure.Unavailable, "MusicBrainz could not be reached.", exception);
            }
            finally
            {
                _last = DateTimeOffset.UtcNow;
                Pace.Release();
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                {
                    throw new MusicBrainzException(MusicBrainzFailure.NotFound, "MusicBrainz does not know this album.");
                }

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    throw new MusicBrainzException(MusicBrainzFailure.RateLimited, "MusicBrainz limits requests.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new MusicBrainzException(MusicBrainzFailure.Unavailable, "MusicBrainz failed.");
                }

                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                }
                catch (JsonException exception)
                {
                    throw new MusicBrainzException(MusicBrainzFailure.InvalidResponse, "MusicBrainz returned invalid JSON.", exception);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException || exception is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    throw new MusicBrainzException(MusicBrainzFailure.Unavailable, "MusicBrainz stopped answering.", exception);
                }
            }
        }

        private static string? Text(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text
                ? text
                : null;

        private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
    }
}
