using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
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

        var visibleTypes = Enum.GetValues<WatchlistMediaType>()
            .Where(type => ToReleaseMediaType(type) is { } mediaType && query.Wants(mediaType))
            .ToArray();
        if (visibleTypes.Length == 0)
        {
            return [];
        }

        var currentAccount = CurrentAccountContext.ForProfile(query.ProfileId);
        var usedExternalIds = new HashSet<string>(StringComparer.Ordinal);
        var events = new List<ReleaseEvent>();

        // Read a bounded SQL page at a time, including franchise inheritance and ignores.
        // Only release events, not every followed work, are kept in the result.
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await watchlist.GetEffectivePageAsync(
                currentAccount,
                new PageRequest(pageNumber, PageRequest.MaximumPageSize),
                visibleTypes,
                cancellationToken);

            var followed = page.Items
                .Where(item => item.Identity.ProviderKey == AniListReleaseNormalizer.Provider)
                .ToArray();

            if (followed.Length > 0)
            {
                var inLibrary = await library.ResolveAsync(
                    followed.Select(item => item.Identity), cancellationToken);
                var byExternalId = followed
                    .Where(item => !inLibrary.ContainsKey(item.Identity.Key))
                    .GroupBy(item => item.Identity.ExternalKey, StringComparer.Ordinal)
                    .Where(group => usedExternalIds.Add(group.Key))
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

                if (byExternalId.Count > 0)
                {
                    var releases = await cache.GetReleasesAsync(
                        AniListReleaseNormalizer.Provider,
                        query.Start.AddDays(-1),
                        query.End.AddDays(1),
                        query.IncludeUndated,
                        byExternalId.Keys.ToArray(),
                        cancellationToken);

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
                }
            }

            if (page.HasMore != true)
            {
                break;
            }
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
