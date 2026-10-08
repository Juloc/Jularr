using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Metadata;

namespace Jularr.Web.Features.Calendar;

public sealed record AnimeReleaseLocalEpisode(Guid EpisodeId, bool HasFile);

/// <summary>
/// One library anime as the calendar needs it: its AniList match, episode-range mappings and
/// local episodes (with whether a file exists).
/// </summary>
public sealed record AnimeReleaseLibraryEntry(
    Guid AnimeId,
    string AnimeKey,
    string Title,
    string? CoverImageUrl,
    string? MatchedExternalId,
    IReadOnlyList<AnimeEpisodeMetadataMapping> Mappings,
    IReadOnlyDictionary<(int Season, int Episode), AnimeReleaseLocalEpisode> Episodes)
{
    /// <summary>Whether releases of this AniList entry belong to the anime.</summary>
    public bool Links(string externalId) =>
        string.Equals(MatchedExternalId, externalId, StringComparison.Ordinal) ||
        Mappings.Any(mapping => mapping.ExternalId == externalId);
}

/// <summary>
/// Merges AniList releases with the canonical library and monitoring state. The local episode
/// slot follows the same rules as the acquisition inventory (episode-range mappings first, the
/// plain match only for single-season anime without mappings); the state comes from the library
/// files, the Wanted queue and the anime's request, never from the calendar.
/// </summary>
public static class AnimeReleaseStateResolver
{
    /// <summary>The local season/episode an AniList episode belongs to, or null when it cannot be placed safely.</summary>
    public static (int Season, int Episode)? ResolveLocalSlot(
        AnimeReleaseLibraryEntry entry,
        string externalId,
        int remoteEpisode)
    {
        var mappings = entry.Mappings
            .Where(mapping => mapping.ExternalId == externalId && remoteEpisode >= mapping.RemoteEpisodeStart)
            .OrderBy(mapping => mapping.SeasonNumber)
            .ThenBy(mapping => mapping.LocalEpisodeStart)
            .ToArray();

        // An explicit range wins over the continuation of a neighbouring one.
        foreach (var mapping in mappings)
        {
            var local = mapping.LocalEpisodeStart + (remoteEpisode - mapping.RemoteEpisodeStart);
            if (local <= mapping.LocalEpisodeEnd)
            {
                return (mapping.SeasonNumber, local);
            }
        }

        foreach (var mapping in mappings)
        {
            if (mapping.EpisodeCount is > 0 && remoteEpisode > mapping.EpisodeCount)
            {
                continue;
            }

            var local = mapping.LocalEpisodeStart + (remoteEpisode - mapping.RemoteEpisodeStart);
            var taken = entry.Mappings.Any(other =>
                !ReferenceEquals(other, mapping) &&
                other.Contains(mapping.SeasonNumber, local));
            if (!taken)
            {
                return (mapping.SeasonNumber, local);
            }
        }

        if (mappings.Length > 0 || entry.Mappings.Count > 0 ||
            !string.Equals(entry.MatchedExternalId, externalId, StringComparison.Ordinal))
        {
            return null;
        }

        var seasons = entry.Episodes.Keys.Select(slot => slot.Season).Distinct().ToArray();
        return seasons.Length switch
        {
            0 => (1, remoteEpisode),
            1 => (seasons[0], remoteEpisode),
            _ => null
        };
    }

    public static ReleaseLocalStatus Resolve(
        AnimeReleaseLibraryEntry entry,
        (int Season, int Episode)? slot,
        AnimeEpisodeStateMap states,
        WorkMonitoringView view,
        bool released)
    {
        if (slot is not { } place)
        {
            return new ReleaseLocalStatus(true, view.IsWorkMonitored, ReleaseLocalState.None);
        }

        var key = new AnimeEpisodeKey(entry.AnimeKey, place.Season, place.Episode);
        var monitored = AnimeMonitoring.IsUnitMonitored(view, key);
        if (entry.Episodes.TryGetValue(place, out var local) && local.HasFile)
        {
            return new ReleaseLocalStatus(true, monitored, ReleaseLocalState.Available);
        }

        switch (states.AttemptOf(key))
        {
            case AcquisitionAttemptStatus.Grabbed:
                return new ReleaseLocalStatus(true, monitored, ReleaseLocalState.Grabbed);
            case AcquisitionAttemptStatus.Failed:
                return new ReleaseLocalStatus(true, monitored, ReleaseLocalState.Failed);
        }

        if (states.WantedOf(key) is not null)
        {
            return new ReleaseLocalStatus(true, monitored, ReleaseLocalState.Wanted);
        }

        var state = (released, monitored) switch
        {
            (true, true) => ReleaseLocalState.Missing,
            (false, true) => ReleaseLocalState.Monitored,
            _ => ReleaseLocalState.NotMonitored
        };
        return new ReleaseLocalStatus(true, monitored, state);
    }

    /// <summary>Calendar events for cached AniList releases of the given library anime.</summary>
    public static IReadOnlyList<ReleaseEvent> ToEvents(
        IEnumerable<CachedRelease> releases,
        IReadOnlyList<AnimeReleaseLibraryEntry> library,
        AnimeEpisodeStateMap states,
        IReadOnlyDictionary<string, WorkMonitoringView> views,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        var events = new List<ReleaseEvent>();
        foreach (var release in releases)
        {
            if (release.Kind is not (ReleaseKind.Episode or ReleaseKind.SeasonPremiere))
            {
                continue;
            }

            foreach (var entry in library.Where(item => item.Links(release.ExternalId)))
            {
                var slot = release.UnitNumber > 0
                    ? ResolveLocalSlot(entry, release.ExternalId, release.UnitNumber)
                    : null;
                var unit = slot is { } place
                    ? new ReleaseUnit(place.Episode, place.Season)
                    : release.UnitNumber > 0 ? new ReleaseUnit(release.UnitNumber) : null;
                var local = slot is { } found && entry.Episodes.TryGetValue(found, out var episode) ? episode : null;

                events.Add(new ReleaseEvent(
                    ReleaseEvent.BuildId(ReleaseMediaType.Anime, entry.AnimeId, release.Kind, unit, release.ExternalId),
                    ReleaseMediaType.Anime,
                    entry.AnimeId,
                    local?.EpisodeId,
                    release.Kind,
                    entry.Title,
                    unit,
                    release.Date,
                    release.Provider,
                    release.ExternalId,
                    Resolve(entry, slot, states, views[entry.AnimeKey], release.Date.IsReleased(now, zone)),
                    entry.CoverImageUrl));
            }
        }

        return events;
    }
}
