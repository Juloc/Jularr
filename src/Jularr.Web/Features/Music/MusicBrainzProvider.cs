using System.Globalization;
using System.Text.Json;
using Jularr.Web.Features.Providers;

namespace Jularr.Web.Features.Music;

/// <summary>
/// MusicBrainz (read-only web service v2, no key). Calls run through the shared provider framework: one request per second as the
/// service asks, bounded retries, Retry-After on HTTP 429 and an identifying User-Agent. Release groups are the album identity; the
/// track list comes from the earliest official release of the group.
/// </summary>
public sealed class MusicBrainzProvider(HttpClient httpClient, ProviderExecutor executor) : IMusicMetadataProvider, IExternalProvider
{
    public const string BaseUrl = "https://musicbrainz.org/ws/2/";
    private const int PageSize = 100;
    private const int MaxPages = 10;

    /// <summary>MusicBrainz allows one request per second per client.</summary>
    public static readonly ProviderExecutionPolicy ExecutionPolicy = new()
    {
        MaxAttempts = 3,
        MinSpacing = TimeSpan.FromMilliseconds(1100)
    };

    public ExternalProviderDescriptor Descriptor { get; } = new(ProviderKeys.MusicBrainz, "MusicBrainz", ProviderCapabilities.Metadata | ProviderCapabilities.Search);

    public async Task<IReadOnlyList<MusicArtistSummary>> SearchArtistsAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        using var document = await GetAsync($"artist/?query={Uri.EscapeDataString(query.Trim())}&limit={Math.Clamp(limit, 1, 50)}&fmt=json", cancellationToken);
        return ReadArray(document, "artists").Select(ReadArtist).OfType<MusicArtistSummary>().ToArray();
    }

    public async Task<IReadOnlyList<MusicAlbumSearchHit>> SearchAlbumsAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        using var document = await GetAsync($"release-group/?query={Uri.EscapeDataString(query.Trim())}&limit={Math.Clamp(limit, 1, 50)}&fmt=json", cancellationToken);
        var hits = new List<MusicAlbumSearchHit>();
        foreach (var element in ReadArray(document, "release-groups"))
        {
            var album = ReadReleaseGroup(element);
            var credit = element.TryGetProperty("artist-credit", out var credits) && credits.ValueKind == JsonValueKind.Array && credits.GetArrayLength() > 0
                ? credits[0]
                : (JsonElement?)null;
            if (album is not null
                && credit is { } first
                && first.TryGetProperty("artist", out var artist)
                && Text(artist, "id") is { } artistId)
            {
                hits.Add(new MusicAlbumSearchHit(album, artistId, Text(artist, "name") ?? Text(first, "name") ?? string.Empty));
            }
        }

        return hits;
    }

    public async Task<MusicArtistSummary?> GetArtistAsync(string musicBrainzId, CancellationToken cancellationToken)
    {
        using var document = await GetAsync($"artist/{Uri.EscapeDataString(musicBrainzId)}?fmt=json", cancellationToken);
        return ReadArtist(document.RootElement);
    }

    public async Task<IReadOnlyList<MusicReleaseGroupSummary>> ListReleaseGroupsAsync(string artistMusicBrainzId, CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, MusicReleaseGroupSummary>(StringComparer.OrdinalIgnoreCase);
        for (var page = 0; page < MaxPages; page++)
        {
            using var document = await GetAsync(
                $"release-group?artist={Uri.EscapeDataString(artistMusicBrainzId)}&type=album|ep&limit={PageSize}&offset={page * PageSize}&fmt=json",
                cancellationToken);
            var items = ReadArray(document, "release-groups");
            foreach (var group in items.Select(ReadReleaseGroup).OfType<MusicReleaseGroupSummary>())
            {
                groups[group.MusicBrainzId] = group;
            }

            var total = document.RootElement.TryGetProperty("release-group-count", out var count) && count.TryGetInt32(out var value) ? value : 0;
            if (items.Count < PageSize || (page + 1) * PageSize >= total)
            {
                break;
            }
        }

        return [.. groups.Values.OrderBy(group => group.ReleaseDate ?? DateTime.MaxValue).ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<MusicAlbumTracks?> GetTracksAsync(string releaseGroupMusicBrainzId, CancellationToken cancellationToken)
    {
        using var document = await GetAsync(
            $"release?release-group={Uri.EscapeDataString(releaseGroupMusicBrainzId)}&status=official&inc=recordings&limit=25&fmt=json",
            cancellationToken);
        var candidates = new List<(string Id, string Date, IReadOnlyList<MusicTrackInfo> Tracks)>();
        foreach (var release in ReadArray(document, "releases"))
        {
            var tracks = ReadTracks(release);
            if (tracks.Count > 0 && Text(release, "id") is { } id)
            {
                candidates.Add((id, Text(release, "date") ?? string.Empty, tracks));
            }
        }

        // The earliest official release with tracks is the canonical track list; a missing date sorts last so a dated one wins.
        var chosen = candidates
            .OrderBy(candidate => candidate.Date.Length == 0 ? 1 : 0)
            .ThenBy(candidate => candidate.Date, StringComparer.Ordinal)
            .ThenByDescending(candidate => candidate.Tracks.Count)
            .FirstOrDefault();
        return chosen.Id is null ? null : new MusicAlbumTracks(chosen.Id, chosen.Tracks);
    }

    private async Task<JsonDocument> GetAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await executor.SendAsync(
            ProviderKeys.MusicBrainz,
            httpClient,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(BaseUrl), relativeUrl));
                request.Headers.Accept.ParseAdd("application/json");
                return request;
            },
            ExecutionPolicy,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new MusicMetadataException($"MusicBrainz answered HTTP {(int)response.StatusCode}.");
        }

        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new MusicMetadataException("MusicBrainz returned an unreadable answer.", exception);
        }
    }

    private static IReadOnlyList<JsonElement> ReadArray(JsonDocument document, string name) =>
        document.RootElement.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? [.. array.EnumerateArray()] : [];

    private static MusicArtistSummary? ReadArtist(JsonElement element) =>
        Text(element, "id") is { } id && Text(element, "name") is { } name
            ? new MusicArtistSummary(id, name, Text(element, "sort-name") ?? name, Text(element, "disambiguation"), Text(element, "country"), Text(element, "type"))
            : null;

    private static MusicReleaseGroupSummary? ReadReleaseGroup(JsonElement element)
    {
        if (Text(element, "id") is not { } id || Text(element, "title") is not { } title)
        {
            return null;
        }

        var secondary = element.TryGetProperty("secondary-types", out var types) && types.ValueKind == JsonValueKind.Array
            ? types.EnumerateArray().Select(type => type.GetString() ?? string.Empty).Where(type => type.Length > 0).ToArray()
            : [];
        var date = ParseDate(Text(element, "first-release-date"));
        return new MusicReleaseGroupSummary(id, title, TypeOf(Text(element, "primary-type"), secondary), date, date?.Year, secondary.Length > 0);
    }

    private static MusicAlbumType TypeOf(string? primary, IReadOnlyList<string> secondary)
    {
        if (secondary.Contains("Live", StringComparer.OrdinalIgnoreCase))
        {
            return MusicAlbumType.Live;
        }

        if (secondary.Contains("Compilation", StringComparer.OrdinalIgnoreCase))
        {
            return MusicAlbumType.Compilation;
        }

        if (secondary.Contains("Soundtrack", StringComparer.OrdinalIgnoreCase))
        {
            return MusicAlbumType.Soundtrack;
        }

        if (secondary.Count > 0)
        {
            return MusicAlbumType.Other;
        }

        return primary?.ToLowerInvariant() switch
        {
            "album" => MusicAlbumType.Album,
            "ep" => MusicAlbumType.Ep,
            "single" => MusicAlbumType.Single,
            _ => MusicAlbumType.Other
        };
    }

    private static IReadOnlyList<MusicTrackInfo> ReadTracks(JsonElement release)
    {
        var tracks = new List<MusicTrackInfo>();
        if (!release.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array)
        {
            return tracks;
        }

        var discIndex = 0;
        foreach (var disc in media.EnumerateArray())
        {
            discIndex++;
            var discNumber = disc.TryGetProperty("position", out var position) && position.TryGetInt32(out var value) && value > 0 ? value : discIndex;
            if (!disc.TryGetProperty("tracks", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var ordinal = 0;
            foreach (var item in items.EnumerateArray())
            {
                ordinal++;
                var number = item.TryGetProperty("position", out var trackPosition) && trackPosition.TryGetInt32(out var parsed) && parsed > 0 ? parsed : ordinal;
                var recording = item.TryGetProperty("recording", out var rec) ? rec : (JsonElement?)null;
                var title = Text(item, "title") ?? (recording is { } known ? Text(known, "title") : null) ?? $"Track {number}";
                int? duration = item.TryGetProperty("length", out var length) && length.ValueKind == JsonValueKind.Number && length.TryGetInt32(out var milliseconds) ? milliseconds : null;
                tracks.Add(new MusicTrackInfo(discNumber, number, title, duration, recording is { } source ? Text(source, "id") : null));
            }
        }

        return tracks;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    /// <summary>A MusicBrainz date may be a year, a month or a day; a partial date counts from its first day.</summary>
    public static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('-');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year < 1000 || year > 9999)
        {
            return null;
        }

        var month = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is >= 1 and <= 12 ? m : 1;
        var day = parts.Length > 2 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d >= 1 && d <= DateTime.DaysInMonth(year, month) ? d : 1;
        return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
    }
}
