using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Jularr.Web.Features.Performance;
using Jularr.Web.Features.Tracking;
using Jularr.Web.Features.Watchlist;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Calendar;

/// <summary>An AniList id the library links to, with the status Jularr last saw for it.</summary>
public sealed record AniListReleaseTarget(int Id, string? KnownStatus, bool OnlyWhenNotYetReleased);

public sealed record ReleaseRefreshResult(int Refreshed, int Failed, int Requests, TimeSpan? RetryAfter);

/// <summary>
/// Bounded refresh of the AniList release cache: only library and followed entries that can still
/// have upcoming releases, only when their cached data is due, at most <see cref="MaxIdsPerRun"/>
/// ids per run. The whole airing schedule of the window is paged through (a weekly show has about
/// 22 airings in it), up to <see cref="MaxPagesPerBatch"/> pages per batch. Requests go through
/// the shared <see cref="AniListRequestLimiter"/>. Failures keep the cached data. This refreshes
/// metadata only; it never marks anything wanted or starts a search.
/// </summary>
public sealed class ReleaseCalendarRefresher(
    AppDbContext db,
    ReleaseCalendarCacheStore cache,
    AniListAccountStore mappings,
    IAniListReleaseScheduleClient client,
    AniListRequestLimiter limiter,
    ILogger<ReleaseCalendarRefresher> logger,
    TimeProvider? clock = null,
    IInstanceModuleService? instanceModules = null)
{
    public const int MaxIdsPerRun = 200;

    /// <summary>50 airings per page: enough for 50 weekly shows over the whole window.</summary>
    public const int MaxPagesPerBatch = 40;
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(12);
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(1);
    public static readonly TimeSpan FinishedRecheckAfter = TimeSpan.FromDays(30);
    public static readonly TimeSpan PastWindow = TimeSpan.FromDays(35);
    public static readonly TimeSpan FutureWindow = TimeSpan.FromDays(120);

    private static readonly string[] EndedStatuses = ["FINISHED", "CANCELLED"];

    public async Task<ReleaseRefreshResult> RefreshDueAsync(CancellationToken cancellationToken)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var sources = await cache.GetSourcesAsync(AniListReleaseNormalizer.Provider, cancellationToken);
        var targets = await CollectTargetsAsync(cancellationToken);
        var due = SelectDue(targets, sources, now.UtcDateTime);
        if (due.Count == 0)
        {
            return new ReleaseRefreshResult(0, 0, 0, null);
        }

        var from = now - PastWindow;
        var to = now + FutureWindow;
        var refreshed = 0;
        var failed = 0;
        var requests = 0;

        foreach (var batch in due.Chunk(AniListReleaseScheduleClient.MaxIdsPerRequest))
        {
            var ids = batch.Select(target => target.Id).ToArray();
            try
            {
                var media = new List<AniListReleaseMedia>();
                var airings = new List<AniListAiring>();
                for (var page = 1; ; page++)
                {
                    if (await limiter.WaitAsync(cancellationToken) is { } blocked)
                    {
                        return new ReleaseRefreshResult(refreshed, failed, requests, blocked);
                    }

                    var schedule = await client.FetchAsync(ids, from, to, page, cancellationToken);
                    requests++;
                    media.AddRange(schedule.Media);
                    airings.AddRange(schedule.Airings);
                    if (!schedule.HasMoreAirings)
                    {
                        break;
                    }

                    if (page == MaxPagesPerBatch)
                    {
                        logger.LogWarning(
                            "The AniList airing schedule of {Count} entries has more than {Pages} pages; later airings are left out.",
                            ids.Length,
                            MaxPagesPerBatch);
                        break;
                    }
                }

                var snapshots = AniListReleaseNormalizer.Normalize(ids, media, airings);
                await cache.SaveAsync(AniListReleaseNormalizer.Provider, snapshots, from, now.UtcDateTime, cancellationToken);
                refreshed += ids.Length;
            }
            catch (ReleaseProviderRateLimitedException limited)
            {
                limiter.RateLimited(limited.RetryAfter);
                logger.LogInformation("AniList rate limit reached; release refresh resumes in {Seconds} s.", limited.RetryAfter.TotalSeconds);
                return new ReleaseRefreshResult(refreshed, failed, requests, limited.RetryAfter);
            }
            catch (MetadataProviderException exception)
            {
                failed += ids.Length;
                logger.LogWarning(exception, "Refreshing AniList release data failed; cached data is kept.");
                await cache.MarkFailedAsync(
                    AniListReleaseNormalizer.Provider,
                    ids.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray(),
                    exception.Message,
                    now.UtcDateTime,
                    cancellationToken);
            }
        }

        return new ReleaseRefreshResult(refreshed, failed, requests, null);
    }

    /// <summary>
    /// Which targets to fetch now: never-fetched entries first, then the oldest data. Ended series
    /// are skipped (and only rechecked rarely), manga and novels only while not yet released
    /// (AniList has no chapter or volume dates, only their start date).
    /// </summary>
    public static IReadOnlyList<AniListReleaseTarget> SelectDue(
        IEnumerable<AniListReleaseTarget> targets,
        IReadOnlyDictionary<string, ReleaseCacheSource> sources,
        DateTime nowUtc)
    {
        return targets
            .GroupBy(target => target.Id)
            .Select(group => new AniListReleaseTarget(
                group.Key,
                group.Select(target => target.KnownStatus).FirstOrDefault(status => status is not null),
                group.All(target => target.OnlyWhenNotYetReleased)))
            .Select(target => (target, source: sources.GetValueOrDefault(target.Id.ToString(CultureInfo.InvariantCulture))))
            .Where(item => IsDue(item.target, item.source, nowUtc))
            .OrderBy(item => item.source?.RefreshedAt ?? DateTime.MinValue)
            .Select(item => item.target)
            .Take(MaxIdsPerRun)
            .ToArray();
    }

    private static bool IsDue(AniListReleaseTarget target, ReleaseCacheSource? source, DateTime nowUtc)
    {
        var status = source?.RefreshedAt is not null ? source.ProviderStatus : target.KnownStatus;
        var ended = status is not null && EndedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);
        if (target.OnlyWhenNotYetReleased && status is not null && !status.Equals("NOT_YET_RELEASED", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (source is null)
        {
            // Never fetched: an entry the library already knows as ended has nothing upcoming.
            return !ended;
        }

        if (source.LastError is not null && source.LastAttemptAt is { } attempted && nowUtc - attempted < RetryFailedAfter)
        {
            return false;
        }

        if (source.RefreshedAt is not { } refreshedAt)
        {
            return true;
        }

        return nowUtc - refreshedAt >= (ended ? FinishedRecheckAfter : RefreshAfter);
    }

    private async Task<IReadOnlyList<AniListReleaseTarget>> CollectTargetsAsync(CancellationToken cancellationToken)
    {
        var targets = new List<AniListReleaseTarget>();
        var instance = instanceModules is null
            ? InstanceModuleSettings.Default
            : await instanceModules.GetAsync(cancellationToken);

        if (instance.IsEnabled(InstanceModule.Anime))
        {
            var anime = await db.AnimeMetadata
                .AsNoTracking()
                .Where(item => item.Provider == AniListReleaseNormalizer.Provider)
                .Select(item => new { item.ExternalId, item.Status })
                .ToListAsync(cancellationToken);
            targets.AddRange(anime
                .Where(item => TryId(item.ExternalId, out _))
                .Select(item => new AniListReleaseTarget(ParseId(item.ExternalId), item.Status, false)));

            foreach (var mapping in await mappings.LoadAllEpisodeMappingsAsync(cancellationToken))
            {
                if (mapping.Provider.Equals(AniListReleaseNormalizer.Provider, StringComparison.OrdinalIgnoreCase) &&
                    TryId(mapping.ExternalId, out var id))
                {
                    targets.Add(new AniListReleaseTarget(id, null, false));
                }
            }
        }

        foreach (var reading in await ReleaseLibraryLinks.LoadReadingLinksAsync(db, cancellationToken))
        {
            if (!instance.IsEnabled(ReleaseInstanceModules.For(reading.MediaType)))
            {
                continue;
            }

            if (TryId(reading.ExternalId, out var id))
            {
                targets.Add(new AniListReleaseTarget(id, reading.Status, true));
            }
        }

        foreach (var followed in await new WatchlistStore(db).GetEffectiveAcrossProfilesAsync(cancellationToken))
        {
            var workType = WorkMediaTypes.FromWatchlist(followed.Identity.MediaType);
            if (!instance.IsEnabled(InstanceModuleMedia.For(workType))
                || !followed.Identity.ProviderKey.Equals(AniListReleaseNormalizer.Provider, StringComparison.OrdinalIgnoreCase)
                || !TryId(followed.Identity.ExternalKey, out var id))
            {
                continue;
            }

            targets.Add(new AniListReleaseTarget(
                id,
                followed.Status,
                followed.Identity.MediaType is WatchlistMediaType.Manga or WatchlistMediaType.LightNovel));
        }

        return targets;
    }

    private static bool TryId(string? value, out int id) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

    private static int ParseId(string value) => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
}

/// <summary>
/// Refreshes the release cache in the background. Runs are bounded by <see cref="ReleaseCalendarRefresher"/>,
/// so a run with nothing due makes no provider call.
/// </summary>
public sealed class ReleaseCalendarRefreshService(
    IServiceScopeFactory scopes,
    ILogger<ReleaseCalendarRefreshService> logger,
    BackgroundWorkGovernor? governor = null) : BackgroundService
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = Interval;
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var result = await governor.RunGovernedAsync(
                        BackgroundWorkClass.ProviderRefresh,
                        "Calendar.Refresh",
                        token => scope.ServiceProvider.GetRequiredService<ReleaseCalendarRefresher>().RefreshDueAsync(token),
                        stoppingToken);
                    if (result.RetryAfter is { } retryAfter && retryAfter > wait)
                    {
                        wait = retryAfter;
                    }
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Refreshing release calendar data failed.");
                }

                await Task.Delay(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
