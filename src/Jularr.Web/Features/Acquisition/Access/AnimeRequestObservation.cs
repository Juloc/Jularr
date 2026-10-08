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
/// A request covers the monitored episodes of its scope that the pipeline tracks and that have aired; it is Completed when all of them
/// have a file and at least one does. Episodes that air later are picked up by the series' monitoring without the request staying open
/// for them. Otherwise it reports the furthest stage of the download in flight. Nothing here writes.
/// </summary>
/// <summary>What an Anime request covers: the slots of its series, the requested ones that have aired, the ones still without a file, and whether nothing can be tracked for it.</summary>
public sealed record AnimeRequestScope(AnimeEpisodeSlots Slots, IReadOnlyList<AnimeEpisodeSlot> Requested, IReadOnlyList<AnimeEpisodeKey> Missing, bool Untracked, string ResultUrl);

public sealed class AnimeRequestObservation(
    AppDbContext db,
    AnimeAcquisitionInventory inventory,
    AnimeAcquisitionPipeline pipeline,
    ReleaseCalendarCacheStore calendar,
    AnimeMonitoring animeMonitoring,
    AcquisitionOwnershipState ownership,
    AnimeAcquisitionSnapshot acquisitions,
    IReadOnlyDictionary<string, ReleaseCacheSource> releaseSources,
    bool searchConfigured,
    DateTime nowUtc) : IRequestObservation
{
    public const string ReadOnlyMessage = "Sonarr manages this series — the owner decides in Sonarr migration.";
    private const string FinishedStatus = "FINISHED";
    private const string LookingMessage = "Looking for the requested episodes.";
    private const string NoEpisodeListMessage = "Not available yet. There is no episode list for this title, so nothing can be searched yet.";
    private const string SearchNotSetUpMessage = "Not available yet. Searching is not set up on this server.";

    public async Task<AcquisitionExecution?> ObserveAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        var scope = await ReadScopeAsync(request, cancellationToken);
        return scope is null ? SeriesMissing(request) : await DecideAsync(request, scope, cancellationToken);
    }

    // An approved request whose series does not exist yet has not been executed: creating the series is the executor's job.
    public static AcquisitionExecution? SeriesMissing(AcquisitionRequest request) =>
        request.Status == AcquisitionRequestStatus.Approved
            ? null
            : new AcquisitionExecution(AcquisitionRequestStatus.Failed, "The series is no longer in the library.");

    /// <summary>The episodes a request covers: its scope of the monitored slots, what has aired, and what of that is still missing. Null when the series does not exist.</summary>
    public async Task<AnimeRequestScope?> ReadScopeAsync(AcquisitionRequest request, CancellationToken cancellationToken)
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
            return null;
        }

        var resultUrl = $"/Library/Anime/{slots.Anime.Id}";
        var airedUpTo = await AiredUpToAsync(slots, cancellationToken);
        var options = request.Options;
        var view = await animeMonitoring.LoadAsync(slots.Anime.Key, cancellationToken);
        var inScope = slots.Slots.Where(slot => options.Includes(slot.Key.SeasonNumber, slot.Key.EpisodeNumber) && AnimeMonitoring.IsUnitMonitored(view, slot.Key)).ToArray();
        var requested = inScope.Where(slot => !IsKnownNotAired(slot, airedUpTo)).ToArray();
        var missing = requested.Where(slot => !slot.HasFile).Select(slot => slot.Key).ToArray();

        // Nothing the pipeline tracks for this request can be searched, or episodes have aired that it does not track at all: the request is not
        // available however many files exist, and it says why instead of looking like a search that is running.
        var untracked = inScope.Length == 0 || (slots.ExpectedEpisodesUnknown && AiredBeyondTracked(slots, airedUpTo));
        return new AnimeRequestScope(slots, requested, missing, untracked, resultUrl);
    }

    /// <summary>Where the request stands against its scope: Completed when everything requested that has aired is in the library, else what the pipeline in flight says.</summary>
    public async Task<AcquisitionExecution> DecideAsync(AcquisitionRequest request, AnimeRequestScope scope, CancellationToken cancellationToken)
    {
        var (slots, requested, missing, untracked, resultUrl) = scope;
        if (!untracked && missing.Count == 0 && requested.Any(slot => slot.HasFile))
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Completed, "The requested episodes are in the library.", ResultUrl: resultUrl);
        }

        if (SonarrParallelSafety.GetMode(ownership, slots.Anime.Key) == AnimeManagementMode.ReadOnlyCoexistence)
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Approved, ReadOnlyMessage, ResultUrl: resultUrl);
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

        var message = untracked ? NoEpisodeListMessage : searchConfigured ? LookingMessage : SearchNotSetUpMessage;
        return new AcquisitionExecution(AcquisitionRequestStatus.Approved, message, ResultUrl: resultUrl);
    }

    /// <summary>
    /// For each AniList entry the series is expected from, the highest episode number known to have aired. A finished entry has aired
    /// completely. Otherwise the cached release calendar says: every episode up to the latest past release has aired, and so has every
    /// one before the next upcoming release, which also covers episodes older than the cache's window. An entry the calendar knows
    /// nothing about has no entry here, so none of its episodes is dropped from the request.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, int>> AiredUpToAsync(AnimeEpisodeSlots slots, CancellationToken cancellationToken)
    {
        var airedUpTo = new Dictionary<string, int>(StringComparer.Ordinal);
        var sources = slots.Slots.Select(slot => slot.SourceExternalId).Append(slots.ExpectedEpisodesUnknown ? slots.MatchedExternalId : null).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        if (sources.Length == 0)
        {
            return airedUpTo;
        }

        var today = DateOnly.FromDateTime(nowUtc);
        var cached = await calendar.GetReleasesAsync(AniListReleaseNormalizer.Provider, today.AddDays(-(int)ReleaseCalendarCacheStore.History.TotalDays), today.AddYears(1), includeUndated: false, sources, cancellationToken);
        foreach (var source in sources)
        {
            var status = releaseSources.TryGetValue(source, out var cachedSource) ? cachedSource.ProviderStatus : null;
            status ??= source == slots.MatchedExternalId ? slots.MatchedStatus : null;
            if (string.Equals(status, FinishedStatus, StringComparison.OrdinalIgnoreCase))
            {
                airedUpTo[source] = int.MaxValue;
                continue;
            }

            var releases = cached
                .Where(release => release.ExternalId == source && release.Kind is ReleaseKind.Episode or ReleaseKind.SeasonPremiere && IsDated(release.Date))
                .ToArray();
            if (releases.Length > 0)
            {
                var latestPast = releases.Where(release => HasAired(release.Date, nowUtc)).Select(release => release.UnitNumber).DefaultIfEmpty(0).Max();
                var nextUpcoming = releases.Where(release => !HasAired(release.Date, nowUtc)).Select(release => release.UnitNumber).DefaultIfEmpty(int.MaxValue).Min();
                airedUpTo[source] = Math.Max(latestPast, nextUpcoming == int.MaxValue ? 0 : nextUpcoming - 1);
            }
        }

        return airedUpTo;
    }

    /// <summary>
    /// For a series that expects no episodes: whether its AniList entry is known to have aired more episodes than the library has. A finished
    /// entry without a count says nothing about how many, so it never counts.
    /// </summary>
    private static bool AiredBeyondTracked(AnimeEpisodeSlots slots, IReadOnlyDictionary<string, int> airedUpTo) =>
        slots.MatchedExternalId is { } source
        && airedUpTo.TryGetValue(source, out var aired)
        && aired != int.MaxValue
        && aired > slots.Slots.Where(slot => slot.HasFile).Select(slot => slot.Key.EpisodeNumber).DefaultIfEmpty(0).Max();

    /// <summary>
    /// An episode is out of the request only when its AniList entry is known and the episode is beyond what has aired and has no file.
    /// An entry nothing is known about (a releasing series the calendar has no row for yet) keeps all its episodes in the request, so
    /// it is not reported complete before every episode the pipeline tracks has a file.
    /// </summary>
    private static bool IsKnownNotAired(AnimeEpisodeSlot slot, IReadOnlyDictionary<string, int> airedUpTo) =>
        !slot.HasFile
        && slot.SourceExternalId is { } source
        && airedUpTo.TryGetValue(source, out var highest)
        && slot.Key.AbsoluteEpisodeNumber is { } number
        && number > highest;

    private static bool IsDated(ReleaseDate date) => date.Instant is not null || date.ExactDay(TimeZoneInfo.Utc) is not null;

    private static bool HasAired(ReleaseDate date, DateTime nowUtc) =>
        date.Instant is { } instant
            ? instant <= nowUtc
            : date.ExactDay(TimeZoneInfo.Utc) is { } day && day <= DateOnly.FromDateTime(nowUtc);
}