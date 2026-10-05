using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Calendar;
using Jularr.Web.Features.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>
/// Where the Anime monitoring pipeline stands for Anime requests, read against the monitoring state, ownership, acquisition relations
/// and release calendar loaded once when the observation began (<see cref="AnimeAcquisitionRequestExecutor.BeginObservationAsync"/>).
/// Completed means every episode the request covers that is monitored and has aired has a file, and at least one does; episodes that
/// have not aired, or whose release is not known, are not part of the request yet, and monitoring keeps picking up later episodes
/// without the request staying open for them. Otherwise it reports the furthest stage of the download in flight. Nothing here writes.
/// </summary>
internal sealed class AnimeRequestObservation(
    AppDbContext db,
    AnimeAcquisitionInventory inventory,
    AnimeAcquisitionPipeline pipeline,
    ReleaseCalendarCacheStore calendar,
    AnimeMonitoringState monitoring,
    AcquisitionOwnershipState ownership,
    AnimeAcquisitionSnapshot acquisitions,
    IReadOnlyDictionary<string, ReleaseCacheSource> releaseSources,
    DateTime nowUtc) : IRequestObservation
{
    private const string FinishedStatus = "FINISHED";
    private const string LookingMessage = "Looking for the requested episodes.";

    public async Task<AcquisitionExecution> ObserveAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        var series = await (
                from match in db.AnimeMetadata.AsNoTracking()
                join item in db.Anime.AsNoTracking() on match.AnimeId equals item.Id
                where match.Provider == AniListMetadataProvider.ProviderKey && match.ExternalId == request.ExternalId
                select item.Key)
            .FirstOrDefaultAsync(cancellationToken);
        var slots = series is null ? null : await inventory.LoadSlotsAsync(series, cancellationToken);
        if (slots is null)
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "The series is no longer in the library.");
        }

        var resultUrl = $"/Library/Anime/{slots.Anime.Id}";
        var released = await ReleasedEpisodesAsync(slots, cancellationToken);
        var options = request.Options;
        var requested = AllSlots(slots, released)
            .Where(slot => options.Includes(slot.Key.SeasonNumber, slot.Key.EpisodeNumber)
                && AnimeMonitoringEngine.IsMonitored(monitoring, slot.Key)
                && HasAired(slots, slot, released))
            .ToArray();
        var missing = requested.Where(slot => !slot.HasFile).Select(slot => slot.Key).ToArray();
        if (missing.Length == 0 && requested.Length > 0)
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Completed, "The requested episodes are in the library.", ResultUrl: resultUrl);
        }

        if (SonarrParallelSafety.GetMode(ownership, slots.Anime.Key) == AnimeManagementMode.ReadOnlyCoexistence)
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Approved, "Sonarr manages this series — the owner decides in Sonarr migration.", ResultUrl: resultUrl);
        }

        var open = await pipeline.ListOpenAcquisitionsAsync(acquisitions, slots.Anime.Key, missing, nowUtc, cancellationToken);
        if (open.FirstOrDefault(item => item.Stage == AnimeOpenAcquisitionStage.Downloading) is { } downloading)
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Downloading, "Download is in progress.", downloading.Download.Id, resultUrl);
        }

        if (open.FirstOrDefault(item => item.Stage == AnimeOpenAcquisitionStage.Importing) is { } importing)
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Importing, "Download complete. Importing into the library.", importing.Download.Id, resultUrl);
        }

        if (open.FirstOrDefault(item => item.Stage == AnimeOpenAcquisitionStage.NeedsOwner) is { } needsOwner)
        {
            var reason = string.IsNullOrWhiteSpace(needsOwner.ImportMessage) ? "The completed download needs a decision from the owner." : needsOwner.ImportMessage;
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, reason, needsOwner.Download.Id, resultUrl);
        }

        return new AcquisitionExecution(AcquisitionRequestStatus.Approved, LookingMessage, ResultUrl: resultUrl);
    }

    /// <summary>The episode numbers each AniList entry of the series has released by now, from the cached release calendar (no provider call).</summary>
    private async Task<IReadOnlyDictionary<string, HashSet<int>>> ReleasedEpisodesAsync(AnimeEpisodeSlots slots, CancellationToken cancellationToken)
    {
        var sources = slots.Slots
            .Select(slot => slot.SourceExternalId)
            .Append(slots.OpenEnded?.ExternalId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (sources.Length == 0)
        {
            return new Dictionary<string, HashSet<int>>();
        }

        var today = DateOnly.FromDateTime(nowUtc);
        var cached = await calendar.GetReleasesAsync(AniListReleaseNormalizer.Provider, today.AddDays(-(int)ReleaseCalendarCacheStore.History.TotalDays), today, includeUndated: false, sources, cancellationToken);
        return cached
            .Where(release => release.Kind is ReleaseKind.Episode or ReleaseKind.SeasonPremiere && HasAired(release.Date, nowUtc))
            .GroupBy(release => release.ExternalId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(release => release.UnitNumber).ToHashSet(), StringComparer.Ordinal);
    }

    /// <summary>
    /// The slots of the series. A matched entry without an episode count only has the episodes it already has, so the episodes the
    /// calendar says have aired are added as missing ones, which keeps the request open until they are downloaded as well.
    /// </summary>
    private static IEnumerable<AnimeEpisodeSlot> AllSlots(AnimeEpisodeSlots slots, IReadOnlyDictionary<string, HashSet<int>> released)
    {
        foreach (var slot in slots.Slots)
        {
            yield return slot;
        }

        if (slots.OpenEnded is not { } openEnded || !released.TryGetValue(openEnded.ExternalId, out var numbers))
        {
            yield break;
        }

        var known = slots.Slots.Where(slot => slot.Key.SeasonNumber == openEnded.Season).Select(slot => slot.Key.EpisodeNumber).ToHashSet();
        foreach (var number in numbers.Where(number => !known.Contains(number)).Order())
        {
            yield return new AnimeEpisodeSlot(new AnimeEpisodeKey(slots.Anime.Key, openEnded.Season, number, number), false, openEnded.ExternalId);
        }
    }

    /// <summary>
    /// A file proves the episode has aired. Without one, it has aired when the calendar lists its release as past, or when its AniList
    /// entry is finished (all its episodes have aired); anything else is not known to have aired and so is not part of the request yet.
    /// </summary>
    private bool HasAired(AnimeEpisodeSlots slots, AnimeEpisodeSlot slot, IReadOnlyDictionary<string, HashSet<int>> released)
    {
        if (slot.HasFile)
        {
            return true;
        }

        if (slot.SourceExternalId is not { } source)
        {
            return false;
        }

        if (slot.Key.AbsoluteEpisodeNumber is { } number && released.TryGetValue(source, out var numbers) && numbers.Contains(number))
        {
            return true;
        }

        var status = releaseSources.TryGetValue(source, out var cached) ? cached.ProviderStatus : null;
        status ??= source == slots.MatchedExternalId ? slots.MatchedStatus : null;
        return string.Equals(status, FinishedStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAired(ReleaseDate date, DateTime nowUtc) =>
        date.Instant is { } instant
            ? instant <= nowUtc
            : date.ExactDay(TimeZoneInfo.Utc) is { } day && day <= DateOnly.FromDateTime(nowUtc);
}
