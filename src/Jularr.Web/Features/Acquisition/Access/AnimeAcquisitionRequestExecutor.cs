using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Naming;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Acquisition.Pipeline;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Calendar;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>
/// Automatic anime acquisition for a request identified by its AniList id, like adding a series
/// in Sonarr: a series that is not in the library yet is created (AniList match, Jularr-managed,
/// first library root, default quality profile), then monitoring and search-on-add are switched
/// on through the acquisition pipeline and the Usenet search starts. The importer later puts
/// the files into the series folder the naming profile builds, and the scan finds this entry by
/// the key of that folder.
/// <para>
/// The request runs the shared lifecycle (Approved, Downloading, Importing, Completed, Failed): <see cref="AnimeRequestScopeReader"/> reads the requested episodes
/// and the library, <see cref="AnimeAcquisitionEngine"/> searches and grabs, and the Wanted pass follows the download and imports it. A request is Completed
/// only when every monitored episode it asks for that has aired has a file; the shared Wanted pass keeps asking until then.
/// </para>
/// <para>
/// The requester's <see cref="AcquisitionRequestOptions"/> are applied when monitoring starts: the
/// chosen quality profile becomes the series' profile, and a request for seasons or single episodes
/// monitors only those (the episodes and seasons the library and AniList already know are switched
/// off, later seasons follow the series-level setting). A title that is already monitored keeps its
/// profile and only gains the requested seasons or episodes.
/// </para>
/// </summary>
public sealed class AnimeAcquisitionRequestExecutor(
    AppDbContext db,
    AnimeMetadataService metadata,
    AnimeNamingProfileStore namingStore,
    AcquisitionOwnershipStore ownershipStore,
    AnimeMonitoringStore monitoringStore,
    AnimeAcquisitionPipeline pipeline,
    AnimeAcquisitionInventory inventory,
    AnimeMonitoring animeMonitoring,
    AnimeAcquisitionEngine engine,
    WantedReconciler wanted,
    LegacyWorkBridge workBridge,
    ReleaseCalendarCacheStore calendar,
    TimeProvider clock) : IAcquisitionRequestExecutor
{
    public MediaAcquisitionKind Kind => MediaAcquisitionKind.Anime;

    public async Task<AcquisitionExecution> ExecuteAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        var anime = await FindSeriesAsync(request.ExternalId, cancellationToken);
        if (anime is not null)
        {
            return await MonitorAsync(request, anime.Id, anime.Key, targetRootId: null, cancellationToken);
        }

        var root = await db.LibraryRoots
            .AsNoTracking()
            .Where(item => item.IsEnabled)
            .OrderBy(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (root is null)
        {
            return new AcquisitionExecution(
                AcquisitionRequestStatus.Failed,
                "No library root is set up. Add one under Admin → System, then retry.");
        }

        var candidate = await metadata.GetCandidateAsync(AniListMetadataProvider.ProviderKey, request.ExternalId, cancellationToken);
        if (candidate is null)
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "AniList has no entry with this id.");
        }

        var created = new Anime { Title = candidate.PreferredTitle };
        created.Key = SeriesKey(await namingStore.LoadAsync(cancellationToken), created, candidate, root);
        if (created.Key.Length == 0)
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, "The naming profile builds an empty series folder name for this title.");
        }

        var existing = await db.Anime.AsNoTracking().SingleOrDefaultAsync(item => item.Key == created.Key, cancellationToken);
        if (existing is not null)
        {
            // A local series of the same name without an AniList match is this series.
            if (await db.AnimeMetadata.AnyAsync(item => item.AnimeId == existing.Id, cancellationToken))
            {
                return new AcquisitionExecution(
                    AcquisitionRequestStatus.Failed,
                    $"The library already has another series in the folder of '{existing.Title}'.",
                    ResultUrl: $"/Library/Anime/{existing.Id}");
            }

            created = existing;
        }
        else
        {
            db.Anime.Add(created);
            await db.SaveChangesAsync(cancellationToken);
        }

        // The Library lists canonical Works, so the series must have one before the first file exists.
        await workBridge.EnsureWorkForAnimeAsync(created, cancellationToken);

        var matched = await metadata.MatchAsync(created.Id, candidate, cancellationToken);
        if (!matched.Success)
        {
            return new AcquisitionExecution(AcquisitionRequestStatus.Failed, matched.Error, ResultUrl: $"/Library/Anime/{created.Id}");
        }

        if (existing is null)
        {
            // Jularr created the series, so Jularr manages it. A series that was already on disk
            // keeps its Sonarr decision.
            await ownershipStore.UpdateAsync(
                state => state.Anime.ContainsKey(created.Key)
                    ? state
                    : SonarrParallelSafety.SetMode(state, created.Key, AnimeManagementMode.JularrManaged, DateTimeOffset.UtcNow),
                cancellationToken);
        }

        return await MonitorAsync(request, created.Id, created.Key, root.Id, cancellationToken);
    }

    // What the request still needs: complete, held back by ownership, or the search for its next episode.
    private async Task<AcquisitionExecution> ContinueAsync(AcquisitionRequest request, CancellationToken cancellationToken)
    {
        var reader = new AnimeRequestScopeReader(
            db,
            inventory,
            calendar,
            animeMonitoring,
            await ownershipStore.LoadAsync(cancellationToken),
            wanted,
            await calendar.GetSourcesAsync(AniListReleaseNormalizer.Provider, cancellationToken),
            clock.GetUtcNow().UtcDateTime);
        var scope = await reader.ReadScopeAsync(request, cancellationToken) ?? throw new InvalidOperationException("The series of the request does not exist after it was added.");
        var decided = reader.Decide(scope);
        return decided.Status == AcquisitionRequestStatus.Approved && decided.Message != AnimeRequestScopeReader.ReadOnlyMessage
            ? await engine.SearchAndGrabAsync(request, scope, decided.Message ?? string.Empty, cancellationToken)
            : decided;
    }

    private async Task<SeriesIdentity?> FindSeriesAsync(string aniListId, CancellationToken cancellationToken) =>
        await (
                from match in db.AnimeMetadata.AsNoTracking()
                join item in db.Anime.AsNoTracking() on match.AnimeId equals item.Id
                where match.Provider == AniListMetadataProvider.ProviderKey && match.ExternalId == aniListId
                select new SeriesIdentity(item.Id, item.Key))
            .FirstOrDefaultAsync(cancellationToken);

    private sealed record SeriesIdentity(Guid Id, string Key);

    // The key the library scan derives from the series folder the importer will create.
    internal static string SeriesKey(AnimeNamingState naming, Anime anime, AnimeMetadataCandidate candidate, LibraryRoot root)
    {
        var rootPath = Path.GetFullPath(root.Path);
        var resolution = AnimeNamingProfileStore.Resolve(naming, anime.Id, root.Id);
        var match = new AnimeMetadata
        {
            AnimeId = anime.Id,
            Provider = candidate.Provider,
            ExternalId = candidate.ExternalId,
            SeasonYear = candidate.SeasonYear
        };
        var folder = AnimeNamingFormatter.BuildSeriesFolderName(
            resolution.Profile,
            AnimeRenameService.BuildSeries(anime, match, rootPath, resolution.SeriesType));
        return MediaPathParser.AnimeKeyForSeriesFolder(folder);
    }

    /// <summary>
    /// Puts the series under monitoring with the requested scope and queues a search for what is missing, then reports where the
    /// request stands. Running it again (an approval, a retry) only repeats the same settings and queues another run, which the
    /// pipeline answers without grabbing an episode that is already downloading or imported.
    /// </summary>
    private async Task<AcquisitionExecution> MonitorAsync(AcquisitionRequest request, Guid animeId, string animeKey, Guid? targetRootId, CancellationToken cancellationToken)
    {
        var options = request.Options;
        if (SonarrParallelSafety.GetMode(await ownershipStore.LoadAsync(cancellationToken), animeKey) == AnimeManagementMode.ReadOnlyCoexistence)
        {
            return await ContinueAsync(request, cancellationToken);
        }

        var existing = (await monitoringStore.LoadAsync(cancellationToken)).Anime.GetValueOrDefault(animeKey);
        var startsMonitoring = !(await animeMonitoring.LoadAsync(animeKey, cancellationToken)).IsWorkMonitored;
        if (startsMonitoring)
        {
            await pipeline.UpdateAnimeSettingsAsync(
                animeId,
                monitored: true,
                searchOnAdd: true,
                profileId: options.QualityProfileId,
                indexerIds: existing?.IndexerIds ?? [],
                cancellationToken,
                targetRootId: existing?.TargetRootId ?? targetRootId);
        }

        // The scope is in place before any search runs, so a search only looks for what was requested.
        await ApplyScopeAsync(animeId, animeKey, options, startsMonitoring, cancellationToken);
        return await ContinueAsync(request, cancellationToken);
    }

    /// <summary>
    /// Restricts monitoring to the requested seasons or episodes. When the request starts monitoring, every
    /// season or episode that is known now and not requested is switched off (for chosen episodes their whole
    /// season, so episodes that air later are not picked up unasked); when the title was monitored already,
    /// the requested ones are only switched on.
    /// </summary>
    private async Task ApplyScopeAsync(
        Guid animeId,
        string animeKey,
        AcquisitionRequestOptions options,
        bool startsMonitoring,
        CancellationToken cancellationToken)
    {
        if (options.Scope == RequestScope.WholeSeries)
        {
            return;
        }

        var known = startsMonitoring
            ? (await inventory.LoadAsync(animeKey, cancellationToken))?.Episodes.Select(episode => episode.Key).ToArray() ?? []
            : [];

        if (startsMonitoring)
        {
            // Nothing is requested yet: start from "nothing is monitored" for every season the title has.
            var seasons = known.Select(episode => episode.SeasonNumber).Concat(options.Episodes.Select(episode => episode.Season)).Distinct().ToArray();
            await animeMonitoring.SetUnitsAsync(animeId, seasons, [], false, cancellationToken);
        }

        await animeMonitoring.SetUnitsAsync(animeId, [.. options.Seasons], [], true, cancellationToken);
        await animeMonitoring.SetUnitsAsync(animeId, [], [.. options.Episodes.Select(episode => (episode.Season, episode.Number))], true, cancellationToken);
    }
}
