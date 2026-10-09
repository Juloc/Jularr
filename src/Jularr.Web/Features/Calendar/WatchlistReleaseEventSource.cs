using Jularr.Web.Features.Watchlist;

namespace Jularr.Web.Features.Calendar;

/// <summary>
/// Projects provider release-cache rows for profile-local followed works that are not in the
/// library. Whether a work is in the library is decided now, from the library's provider matches,
/// so a work added to the library later shows only its library events. The follow state is owned
/// by Jularr; provider data only supplies release dates.
/// </summary>
public sealed class WatchlistReleaseEventSource(
    ReleaseCalendarCacheStore cache,
    WatchlistStore watchlist,
    WatchlistLibraryResolver library) : IReleaseEventSource
{
    public string Name => "watchlist";

    /// <summary>Only what the cached AniList release data can produce.</summary>
    public IReadOnlyCollection<ReleaseMediaType> MediaTypes { get; } =
        [ReleaseMediaType.Anime, ReleaseMediaType.Manga, ReleaseMediaType.LightNovel];

    public async Task<IReadOnlyList<ReleaseEvent>> GetEventsAsync(
        ReleaseEventQuery query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.ProfileId))
        {
            return [];
        }

        var followed = (await watchlist.GetEffectiveAsync(query.ProfileId, cancellationToken))
            .Where(item => item.Identity.ProviderKey == AniListReleaseNormalizer.Provider)
            .Where(item => ToReleaseMediaType(item.Identity.MediaType) is { } type && query.Wants(type))
            .ToArray();
        if (followed.Length == 0)
        {
            return [];
        }

        var inLibrary = await library.ResolveAsync(followed.Select(item => item.Identity), cancellationToken);
        var byExternalId = followed
            .Where(item => !inLibrary.ContainsKey(item.Identity.Key))
            .GroupBy(item => item.Identity.ExternalKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (byExternalId.Count == 0)
        {
            return [];
        }

        var releases = await cache.GetReleasesAsync(
            AniListReleaseNormalizer.Provider,
            query.Start.AddDays(-1),
            query.End.AddDays(1),
            query.IncludeUndated,
            byExternalId.Keys.ToArray(),
            cancellationToken);

        var events = new List<ReleaseEvent>();
        foreach (var release in releases)
        {
            if (!byExternalId.TryGetValue(release.ExternalId, out var item) ||
                ToReleaseMediaType(item.Identity.MediaType) is not { } mediaType ||
                !KindFits(mediaType, release.Kind) ||
                !query.Includes(release.Date))
            {
                continue;
            }

            var unit = release.Kind is ReleaseKind.Episode or ReleaseKind.SeasonPremiere or ReleaseKind.Chapter or ReleaseKind.Volume &&
                       release.UnitNumber > 0
                ? new ReleaseUnit(release.UnitNumber)
                : null;
            events.Add(new ReleaseEvent(
                ReleaseEvent.BuildId(mediaType, item.StableId, release.Kind, unit, release.Provider),
                mediaType,
                item.StableId,
                null,
                release.Kind,
                item.Title,
                unit,
                release.Date,
                release.Provider,
                release.ExternalId,
                ReleaseLocalStatus.Following,
                item.CoverImageUrl,
                DetailsUrl: item.Identity.WatchlistUrl));
        }

        return events;
    }

    private static ReleaseMediaType? ToReleaseMediaType(WatchlistMediaType type) => type switch
    {
        WatchlistMediaType.Anime => ReleaseMediaType.Anime,
        WatchlistMediaType.Manga => ReleaseMediaType.Manga,
        WatchlistMediaType.LightNovel => ReleaseMediaType.LightNovel,
        _ => null
    };

    private static bool KindFits(ReleaseMediaType type, ReleaseKind kind) => type switch
    {
        ReleaseMediaType.Anime => kind is ReleaseKind.Episode or ReleaseKind.SeasonPremiere,
        ReleaseMediaType.Manga or ReleaseMediaType.LightNovel =>
            kind is ReleaseKind.SeriesStart or ReleaseKind.Chapter or ReleaseKind.Volume,
        _ => false
    };
}
