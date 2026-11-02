using System.Globalization;
using System.Text.Json;
using Jularr.Integrations.Data.AniList;

namespace Jularr.Integrations.Logic.AniList
{
    // A person's own AniList account: the sign-in with an authorization code, their list and the progress written back. It shares the pace and the rate limit of the catalog calls.
    internal sealed partial class AniListClient
    {
        private const int ListChunk = 500;
        private const int ListChunkLimit = 20;

        // The moment AniList allows requests again after it limited them; null when nothing holds them.
        public static DateTimeOffset? BlockedUntil => new DateTimeOffset(Interlocked.Read(ref _blockedUntilTicks), TimeSpan.Zero) is var until && until > DateTimeOffset.UtcNow ? until : null;

        // The authorization code of the PIN page becomes an access token; the secret travels once and is never kept here.
        public async Task<AniListToken> ExchangeCodeAsync(Uri tokenEndpoint, Uri redirectUri, string clientId, string clientSecret, string code, CancellationToken ct)
        {
            using var document = await PostAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["redirect_uri"] = redirectUri.AbsoluteUri,
                ["code"] = code
            }, tokenEndpoint, null, false, ct);
            var root = document.RootElement;
            if (Text(root, "access_token") is not { } token)
            {
                throw new AniListException(AniListFailure.InvalidResponse, "AniList returned no token.");
            }

            // The lifetime comes in seconds; AniList's tokens last a year.
            DateTimeOffset? expiresAt = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("expires_in", out var lifetime) && lifetime.ValueKind == JsonValueKind.Number && lifetime.TryGetInt64(out var seconds) && seconds > 0
                ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                : null;
            return new AniListToken(token, expiresAt);
        }

        public async Task<AniListViewer> ReadViewerAsync(string token, CancellationToken ct)
        {
            using var document = await PostAsync("query { Viewer { id name } }", new Dictionary<string, object>(), ct, token);
            return Child(Child(document.RootElement, "data"), "Viewer") is { } viewer && viewer.TryGetProperty("id", out var id) && id.TryGetInt32(out var viewerId) && viewerId > 0 && Text(viewer, "name") is { } name
                ? new AniListViewer(viewerId, name)
                : throw new AniListException(AniListFailure.InvalidResponse, "AniList did not name the person.");
        }

        // The whole anime or manga list, chunk by chunk; an entry that sits on several lists counts once.
        public async Task<IReadOnlyList<AniListListEntry>> ReadListAsync(string token, int userId, AniListMediaType type, CancellationToken ct)
        {
            var entries = new Dictionary<int, AniListListEntry>();
            for (var chunk = 1; chunk <= ListChunkLimit; chunk++)
            {
                using var document = await PostAsync("query ($userId: Int, $type: MediaType, $chunk: Int, $perChunk: Int) { MediaListCollection(userId: $userId, type: $type, chunk: $chunk, perChunk: $perChunk) { hasNextChunk "
                    + "lists { entries { id mediaId status progress progressVolumes score(format: POINT_100) updatedAt media { episodes chapters volumes title { romaji english } format startDate { year } coverImage { large } } } } } }",
                    new Dictionary<string, object> { ["userId"] = userId, ["type"] = Name(type), ["chunk"] = chunk, ["perChunk"] = ListChunk }, ct, token);
                if (Child(Child(document.RootElement, "data"), "MediaListCollection") is not { } collection)
                {
                    throw new AniListException(AniListFailure.InvalidResponse, "AniList did not answer with a list.");
                }

                if (collection.TryGetProperty("lists", out var lists) && lists.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in lists.EnumerateArray().Where(list => list.ValueKind == JsonValueKind.Object && list.TryGetProperty("entries", out var rows) && rows.ValueKind == JsonValueKind.Array)
                        .SelectMany(list => list.GetProperty("entries").EnumerateArray()).Select(row => ListEntry(row, type == AniListMediaType.Manga)).OfType<AniListListEntry>())
                    {
                        entries.TryAdd(entry.MediaId, entry);
                    }
                }

                if (!(collection.TryGetProperty("hasNextChunk", out var more) && more.ValueKind == JsonValueKind.True))
                {
                    break;
                }
            }

            return [.. entries.Values];
        }

        // Only progress changes (and the start of a planned entry); no status, score, note or date besides that is ever written.
        public async Task<AniListSavedEntry> SaveProgressAsync(string token, long listEntryId, bool volumes, int progress, bool startsTracking, DateTimeOffset now, CancellationToken ct)
        {
            var field = volumes ? "progressVolumes" : "progress";
            var variables = new Dictionary<string, object> { ["id"] = listEntryId, ["progress"] = progress };
            string query;
            if (startsTracking)
            {
                variables["year"] = now.Year;
                variables["month"] = now.Month;
                variables["day"] = now.Day;
                query = $"mutation ($id: Int, $progress: Int, $year: Int, $month: Int, $day: Int) {{ SaveMediaListEntry(id: $id, {field}: $progress, status: CURRENT, startedAt: {{ year: $year, month: $month, day: $day }}) {{ id progress progressVolumes updatedAt }} }}";
            }
            else
            {
                query = $"mutation ($id: Int, $progress: Int) {{ SaveMediaListEntry(id: $id, {field}: $progress) {{ id progress progressVolumes updatedAt }} }}";
            }

            using var document = await PostAsync(query, variables, ct, token);
            return Child(Child(document.RootElement, "data"), "SaveMediaListEntry") is { } saved
                && saved.TryGetProperty("progress", out var episodes) && episodes.TryGetInt32(out var savedProgress)
                && saved.TryGetProperty("progressVolumes", out var books) && books.TryGetInt32(out var savedVolumes)
                    ? new AniListSavedEntry(savedProgress, savedVolumes, UnixTime(saved) ?? now)
                    : throw new AniListException(AniListFailure.InvalidResponse, "AniList did not confirm the entry.");
        }

        private static AniListListEntry? ListEntry(JsonElement row, bool isManga)
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("mediaId", out var mediaId) || !mediaId.TryGetInt32(out var id) || id <= 0
                || !row.TryGetProperty("id", out var entryId) || !entryId.TryGetInt64(out var listEntryId) || listEntryId <= 0 || Text(row, "status") is not { } status
                || Child(row, "media") is not { } media)
            {
                return null;
            }

            var title = Child(media, "title") is { } names ? Text(names, "english") ?? Text(names, "romaji") : null;
            if (title is null || UnixTime(row) is not { } updatedAt)
            {
                return null;
            }

            int Whole(string property) => row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0 ? number : 0;
            var points = row.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number && score.TryGetDouble(out var raw) ? (int)Math.Clamp(Math.Round(raw), 0, 100) : 0;
            var cover = Https(Child(media, "coverImage") is { } image ? Text(image, "large") : null);
            var year = Child(media, "startDate") is { } start ? Count(start, "year", 2200) : null;
            return new AniListListEntry(id, listEntryId, isManga, title.Length > 500 ? title[..500] : title, cover is { Length: <= 500 } ? cover : null, Text(media, "format") == "NOVEL", year is >= 1800 ? year : null,
                status, Whole("progress"), Whole("progressVolumes"), points, Count(media, isManga ? "chapters" : "episodes", 100_000), Count(media, "volumes", 10_000), updatedAt);
        }

        private static DateTimeOffset? UnixTime(JsonElement element) =>
            element.TryGetProperty("updatedAt", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds) && seconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
    }
}
