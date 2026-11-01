using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Integrations.Data.Newznab;
using Jularr.Integrations.Data.AnnasArchive;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Data.Qbittorrent;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Sabnzbd;
using Jularr.Integrations.Logic.Qbittorrent;
using Jularr.Integrations.Logic.Storage;
using Jularr.Integrations.Logic.Torrent;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Data.Enums.Sql;
using Microsoft.AspNetCore.DataProtection;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Logic.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Admin.Wanted.V1
{
    // Indexer and client I/O of one wanted Work; the decisions belong to AcquisitionSelectionLogic.
    internal sealed class WantedAcquisition
    {
        private const int PageLimit = 100;
        private static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(30);

        private readonly NewznabClient _newznab;
        private readonly SabnzbdClient _sabnzbd;
        private readonly QbittorrentClient _qbittorrent;
        private readonly DirectSources _direct;
        private readonly NyaaSources _nyaa;
        private readonly IDataProtector _protector;
        private readonly IMemoryCache _offers;
        private readonly TimeProvider _time;
        private readonly DownloadStorage _downloads;
        private readonly StorageAccess _storage;
        private readonly WorkSignals _signals;
        private readonly ILogger<WantedAcquisition> _logger;

        public WantedAcquisition(NewznabClient newznab, SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, DirectSources direct, NyaaSources nyaa,
            IDataProtectionProvider protection, IMemoryCache offers, TimeProvider time, DownloadStorage downloads, StorageAccess storage, WorkSignals signals, ILogger<WantedAcquisition> logger)
        {
            _logger = logger;
            _storage = storage;
            _signals = signals;
            _downloads = downloads;
            _qbittorrent = qbittorrent;
            _time = time;
            _newznab = newznab;
            _sabnzbd = sabnzbd;
            _direct = direct;
            _nyaa = nyaa;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
            _offers = offers;
        }

        // Automatic searches leave out manual-only, backed-off and exhausted indexers; every indexer asked records its use.
        public async Task<WantedSearchResult> SearchAsync(LogicContext context, WantedTargetData target, IReadOnlyList<AcquisitionConnectionData> indexers, SearchDepth depth, bool automatic,
            CancellationToken ct, UnitScope? scope = null, string? focusLanguage = null)
        {
            // Only targets with a profile can be ranked.
            if (target.Definition is null)
            {
                throw new DataValidationException(nameof(WantedItemParameters.WantedItemId), "invalid_transition");
            }

            var kind = AcquisitionSelectionLogic.GetKind(target.MediaTypeId, target.MediaSubtypeId);

            var now = _time.GetUtcNow().UtcDateTime;
            var profile = AcquisitionSelectionLogic.ReadProfile(target);
            var intent = AcquisitionSelectionLogic.ReadIntent(target, kind, scope);
            var budget = SearchBudget.For(depth);
            var offers = new ConcurrentDictionary<string, ReleaseOffer>(StringComparer.Ordinal);
            var failures = 0;
            var sourceErrors = new ConcurrentBag<string>();
            using var annaSearch = new AnnasArchiveSearchContext();
            var allNewznab = indexers.Where(indexer => indexer.AcquisitionConnectionTypeId == AcquisitionConnectionType.Newznab).ToArray();
            var newznab = allNewznab.Where(indexer => AcquisitionConnectionLogic.CanSearch(indexer, target.MediaTypeId, automatic, now)).ToArray();
            var skipped = allNewznab.Count(indexer => AcquisitionConnectionLogic.Serves(indexer, target.MediaTypeId, automatic)) - newznab.Length;
            var uses = new ConcurrentDictionary<long, IndexerUse>();
            var catalogs = kind is AcquisitionKind.Book or AcquisitionKind.LightNovel ? indexers.Where(indexer => DirectSources.IsDirect(indexer.AcquisitionConnectionTypeId)
                && (kind == AcquisitionKind.LightNovel || indexer.AcquisitionConnectionTypeId != AcquisitionConnectionType.Narou)).ToArray() : [];
            var nyaaConnections = kind is AcquisitionKind.LightNovel ? indexers.Where(indexer => indexer.AcquisitionConnectionTypeId == AcquisitionConnectionType.Nyaa).ToArray() : [];
            var torrents = nyaaConnections.Where(indexer => AcquisitionConnectionLogic.CanSearch(indexer, target.MediaTypeId, automatic, now)).ToArray();
            skipped += nyaaConnections.Count(indexer => AcquisitionConnectionLogic.Serves(indexer, target.MediaTypeId, automatic)) - torrents.Length;
            var plans = newznab.ToDictionary(indexer => indexer.Id, indexer => SearchPlanner.Plan(intent, ReadCapabilities(indexer), depth));

            // Direct book sources answer one title and author query; they are asked at the same time as the indexers.
            var direct = SearchDirectSourcesAsync(catalogs, target, budget, offers, ct);
            var nyaaSearch = SearchNyaaSourcesAsync(torrents, target, budget, offers, ct);

            // The torrent index answers for the light novels; it is asked at the same time as the indexers.
            async Task SearchNyaaSourcesAsync(IReadOnlyList<AcquisitionConnectionData> sources, WantedTargetData wanted, SearchBudget limits,
                ConcurrentDictionary<string, ReleaseOffer> found, CancellationToken token)
            {
                await Parallel.ForEachAsync(sources, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, sources.Count), CancellationToken = token },
                    async (source, inner) =>
                    {
                        var use = uses.GetOrAdd(source.Id, _ => new IndexerUse());
                        if (!AcquisitionConnectionLogic.HasQueries(source, use.Queries, automatic)) return;
                        Interlocked.Increment(ref use.Queries);
                        if (await _nyaa.SearchAsync(source, wanted, inner) is not { } answers)
                        {
                            use.Failed = true;
                            Interlocked.Increment(ref failures);
                            return;
                        }

                        foreach (var offer in answers.Take(limits.MaxResults))
                        {
                            found.TryAdd(offer.Identity, offer);
                        }
                    });
            }

            // The direct sources answer at the same time as the indexers; each of them on its own.
            async Task SearchDirectSourcesAsync(IReadOnlyList<AcquisitionConnectionData> sources, WantedTargetData wanted, SearchBudget limits,
                ConcurrentDictionary<string, ReleaseOffer> found, CancellationToken token)
            {
                await Parallel.ForEachAsync(sources, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, sources.Count), CancellationToken = token },
                    async (source, inner) =>
                    {
                        // Include waiting for Chrome and every title/ISBN variant in the source's budget.
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(inner);
                        timeout.CancelAfter(limits.IndexerTimeout);
                        IReadOnlyList<ReleaseOffer>? answers;
                        try
                        {
                            answers = await _direct.SearchAsync(source, wanted, timeout.Token, Enough, annaSearch);
                        }
                        catch (OperationCanceledException) when (!inner.IsCancellationRequested)
                        {
                            sourceErrors.Add("source_timeout");
                            Interlocked.Increment(ref failures);
                            return;
                        }
                        catch (DirectSourceException exception)
                        {
                            sourceErrors.Add(exception.ErrorKey);
                            Interlocked.Increment(ref failures);
                            return;
                        }

                        if (answers is null)
                        {
                            Interlocked.Increment(ref failures);
                            return;
                        }

                        foreach (var offer in answers.Take(limits.MaxResults))
                        {
                            found.TryAdd(offer.Identity, offer);
                        }
                    });
            }

            // One way of one source is enough when the choice already has something to take; the indexers count too.
            bool Enough(IReadOnlyList<ReleaseOffer> found)
            {
                var all = found.Concat(offers.Values).ToArray();
                return AcquisitionSelectionLogic.Select(profile, target, kind, scope, all, focusLanguage, automatic).Ranked.Any(evaluation =>
                    evaluation.IsSelectable && (!profile.IsBook || profile.LanguageOrder.Length == 0
                        || UpgradePolicy.IsRequested(profile, evaluation.Candidate.Languages)));
            }

            var selection = AcquisitionSelectionLogic.Select(profile, target, kind, scope, offers.Values, focusLanguage, automatic);

            async Task SearchIndexersAsync()
            {
                // Expansion tiers only run while too few releases are usable.
                for (var tier = 0; tier <= budget.MaxTier && offers.Count < budget.MaxResults; tier++)
                {
                    var current = tier;
                    if (!plans.Values.Any(plan => plan.Any(query => query.Tier == current)))
                    {
                        continue;
                    }

                    await Parallel.ForEachAsync(newznab, new ParallelOptions { MaxDegreeOfParallelism = budget.MaxConcurrentIndexers, CancellationToken = ct }, async (indexer, token) =>
                    {
                        var use = uses.GetOrAdd(indexer.Id, _ => new IndexerUse());
                        if (!await SearchIndexerAsync(indexer, kind, target.PlatformIgdbId, plans[indexer.Id].Where(query => query.Tier == current), budget, offers, use, automatic, token))
                        {
                            use.Failed = true;
                            Interlocked.Increment(ref failures);
                        }
                    });

                    // The direct sources answered at the same time; their releases belong to the choice.
                    await direct;
                    await nyaaSearch;
                    selection = AcquisitionSelectionLogic.Select(profile, target, kind, scope, offers.Values, focusLanguage, automatic);
                    if (selection.Ranked.Count(evaluation => evaluation.IsSelectable
                        && (!profile.IsBook || profile.LanguageOrder.Length == 0
                            || UpgradePolicy.IsRequested(profile, evaluation.Candidate.Languages))) >= budget.TargetUsable)
                    {
                        break;
                    }
                }
            }

            // Join every source on cancellation before disposing their shared Anna search context.
            await Task.WhenAll(direct, nyaaSearch, SearchIndexersAsync());
            selection = AcquisitionSelectionLogic.Select(profile, target, kind, scope, offers.Values, focusLanguage, automatic);

            // Manual grabs pick from what was shown; the cache is bounded by offer count.
            var grabbable = selection.Ranked.Where(evaluation => evaluation.Decision != SelectionDecision.Rejected).Select(evaluation => evaluation.Candidate.Id).ToHashSet(StringComparer.Ordinal);
            var cached = offers.Where(pair => grabbable.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal);
            _offers.Set(CacheKey(target.WantedItemId), cached, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = OfferLifetime, Size = Math.Max(1, cached.Count) });

            // Every release group the search saw joins the media type's list, so it can be rated before anything is taken.
            var groups = ReleaseGroupLogic.Sightings(selection.Ranked.Select(evaluation => evaluation.Candidate.Parsed?.ReleaseGroup));
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                foreach (var (id, use) in uses)
                {
                    await ctx.RecordIndexerUseAsync(new IndexerUseData(id, use.Queries, !use.Failed, use.RetryAfterSeconds > 0 ? use.RetryAfterSeconds : null), token);
                }

                await ctx.RecordReleaseGroupsAsync(new ReleaseGroupSightingsData(target.MediaTypeId, groups, now), token);
                return true;
            }, ct);

            // Indexers left out for backoff or limits count as unavailable, not as empty.
            var sources = catalogs.Length + torrents.Length + newznab.Length + skipped;
            var unavailable = sources > 0 && failures + skipped >= sources && offers.IsEmpty;
            var errorKey = unavailable && skipped == 0 && sourceErrors.Count == failures && sourceErrors.Distinct().Count() == 1 ? sourceErrors.First() : null;
            return new WantedSearchResult(selection, offers, unavailable, AcquisitionSelectionLogic.OutcomeKey(selection.Outcome, unavailable), errorKey);
        }

        public ReleaseOffer? FindOffer(long wantedItemId, string candidateId) =>
            _offers.TryGetValue(CacheKey(wantedItemId), out Dictionary<string, ReleaseOffer>? offers) && offers!.TryGetValue(candidateId, out var offer) ? offer : null;

        // Now instead of on schedule: the profile chooses as the automatic search does and the best release is taken at once.
        public async Task<AutomaticSearchResultV1> SearchAutomaticallyAsync(LogicContext context, long wantedItemId, int? parentUnitNumber, int? unitNumber, string? language, long? accountId, CancellationToken ct)
        {
            var (target, indexers, clients) = await context.ExecuteBatchAsync(async (ctx, token) => (await ctx.ReadWantedTargetAsync(wantedItemId, token),
                await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Search, token), await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Download, token)), ct);
            if (target.Definition is null)
            {
                return new AutomaticSearchResultV1("profile_missing", null);
            }

            if (target.IsDownloading && target.MediaTypeId != MediaType.Series)
            {
                throw new DataValidationException(nameof(WantedItemParametersV1.WantedItemId), "in_use");
            }

            var scope = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadSearchScopeAsync(target, parentUnitNumber, unitNumber, token), ct);
            var result = await SearchAsync(context, target, indexers, SearchDepth.Normal, automatic: true, ct, scope, language);
            if (result.IndexersUnavailable)
            {
                throw new ServiceUnavailableException(result.ErrorKey ?? "acquisition_unavailable");
            }

            if (result.Selection.Winner is not { } winner)
            {
                return new AutomaticSearchResultV1(result.OutcomeKey, null);
            }

            var offer = result.Offers[winner.Candidate.Id];
            await GrabAsync(context, target, offer, winner.Score?.QualityKey, clients, ct, accountId);
            return new AutomaticSearchResultV1("grabbed", TextLimit.Cut(offer.Title, 1000));
        }

        // A queued whole-title search reassesses installed evidence and claims each missing or upgradable unit once.
        public async Task<string> SearchWholeTargetAsync(LogicContext context, long wantedItemId, CancellationToken ct)
        {
            var (target, indexers, clients) = await context.ExecuteBatchAsync(async (ctx, token) => (await ctx.ReadWantedTargetAsync(wantedItemId, token),
                await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Search, token), await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Download, token)), ct);
            if (target.IsPaused) return "paused";
            if (target.Definition is null) return "profile_missing";
            var profile = AcquisitionSelectionLogic.ReadProfile(target);
            var kind = AcquisitionSelectionLogic.GetKind(target.MediaTypeId, target.MediaSubtypeId);
            if (target.MediaTypeId != MediaType.Series && target.MediaSubtypeId != MediaSubtype.Manga)
            {
                if (target.IsDownloading) return "in_use";
                if (target.HasMedia)
                {
                    if (AcquisitionSelectionLogic.Installed(profile, target) is not { } installed) return "evidence_missing";
                    if (!AcquisitionSelectionLogic.NeedsUpgrade(profile, installed)) return "unchanged";
                }
                var result = await SearchAsync(context, target, indexers, SearchDepth.Normal, automatic: true, ct);
                if (result.Selection.Winner is not { } winner) return result.OutcomeKey;
                await GrabAsync(context, target, result.Offers[winner.Candidate.Id], winner.Score?.QualityKey, clients, ct, automatic: true);
                return "grabbed";
            }

            var units = AcquisitionSelectionLogic.ReassessUnits(profile, target, await context.ExecuteBatchAsync((ctx, token) => ctx.ReadUnitStatesAsync(wantedItemId, token), ct));
            var grabbed = false;
            var outcome = "unchanged";
            foreach (var parent in units.Where(unit => unit.NeedsSearch).Select(unit => unit.ParentNumber).Distinct().ToArray())
            {
                var first = true;
                while (AcquisitionSelectionLogic.ReadNextUnitScope(units, parent, first) is { } scope)
                {
                    ct.ThrowIfCancellationRequested();
                    first = false;
                    indexers = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Search, token), ct);
                    var result = await SearchAsync(context, target, indexers, SearchDepth.Normal, automatic: true, ct, scope);
                    if (result.Selection.Winner is { } winner)
                    {
                        await GrabAsync(context, target, result.Offers[winner.Candidate.Id], winner.Score?.QualityKey, clients, ct, automatic: true);
                        grabbed = true;
                        units = AcquisitionSelectionLogic.Claim(units, scope, kind, winner.Candidate.Parsed);
                    }
                    else
                    {
                        outcome = result.OutcomeKey;
                        // An empty pack search still leaves single-unit queries; each unit is tried at most once in this run.
                        units = units.Select(unit => unit.WorkUnitId == scope.Unit.Id
                            ? new UnitStateData(unit.WorkUnitId, unit.ParentNumber, unit.Number, unit.IsAvailable, unit.HasMedia, true, unit.AbsoluteNumber, unit.ReleasedOn, unit.NeedsUpgrade, unit.Title)
                            : unit).ToArray();
                        if (result.IndexersUnavailable) break;
                    }
                }
            }

            return grabbed ? "grabbed" : outcome;
        }

        public async Task GrabAsync(LogicContext context, WantedTargetData target, ReleaseOffer offer, string? qualityKey, IReadOnlyList<AcquisitionConnectionData> clients, CancellationToken ct,
            long? accountId = null, bool automatic = false)
        {
            // A direct-source book writes to Jularr's own workspace. Check the actual path
            // before creating an operation; a missing mount must never fall back to the VM.
            if (DirectSources.IsDirectOffer(offer.Guid) && offer.DownloadUri is { } source)
            {
                var paths = await _downloads.OpenAsync(context, DownloadStorageArea.Direct, ct);
                var direct = new AcquisitionGrabData(target.WantedItemId, offer.ConnectionId, Limit(offer.Identity, 600), Limit(offer.Title, 1000), qualityKey, offer.ConnectionId, null, null, null,
                    source.ToString(), accountId) { RequiresActiveMonitoring = automatic };
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    await ctx.RequireDownloadStorageSettingsAsync(paths.Settings, token);
                    var reserved = await ctx.ReserveDownloadAsync(direct, token);
                    await ctx.StartDownloadAsync(new DownloadStartData(reserved, Limit(offer.Guid!, 128)), token);
                    return true;
                }, ct);

                // Tracking and the downloader look at the new download at once.
                _signals.Downloads.Notify();
                return;
            }

            // Select a downloader only after filtering by offer transport and media coverage.
            var isTorrent = offer.Transport == DownloadTransport.Torrent
                || offer.DownloadUri?.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase) == true
                || offer.DownloadUri?.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) == true;
            // Jularr's own torrent engine takes torrents only while its module is on.
            var compatibleTypes = !isTorrent ? new[] { AcquisitionConnectionType.Sabnzbd, AcquisitionConnectionType.Usenet }
                : (await context.ExecuteBatchAsync((ctx, token) => ctx.ReadModuleStatesAsync(new InstanceModuleParameters(["Torrent"]), token), ct, SqlAccessMode.ReadOnly)).Any(module => module.IsEnabled)
                    ? new[] { AcquisitionConnectionType.Qbittorrent, AcquisitionConnectionType.Torrent }
                    : new[] { AcquisitionConnectionType.Qbittorrent };
            var client = DownloadClientSelectionLogic.Select(clients, target.MediaTypeId, _time.GetUtcNow().UtcDateTime, compatibleTypes);
            if (client is null)
            {
                throw new ServiceUnavailableException("acquisition_no_download_client");
            }

            if (offer.DownloadUri is null)
            {
                throw new DataValidationException("CandidateId", "invalid_transition");
            }

            // SABnzbd owns the download folder. Do not send a release that Jularr cannot import.
            if (client.AcquisitionConnectionTypeId is AcquisitionConnectionType.Sabnzbd or AcquisitionConnectionType.Qbittorrent)
            {
                await RequireClientImportSourceAsync(context, client, ct);
            }

            // Refuse native downloads before reserving an operation or using an indexer grab.
            DownloadStoragePaths? nativePaths = null;
            if (client.AcquisitionConnectionTypeId is AcquisitionConnectionType.Usenet or AcquisitionConnectionType.Torrent)
            {
                nativePaths = await _downloads.OpenAsync(context, client.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet ? DownloadStorageArea.Usenet : DownloadStorageArea.Torrent, ct);
            }

            // A grab counts against its indexer's daily limit; a download added by hand (connection 0) has no indexer.
            var manual = offer.ConnectionId <= 0;
            if (!manual && !await context.ExecuteBatchAsync((ctx, token) => ctx.CountIndexerGrabAsync(new AcquisitionConnectionParameters(offer.ConnectionId), token), ct))
            {
                throw new ServiceUnavailableException("acquisition_limit_reached");
            }

            // Reserve first so concurrent grabs cannot start a second download.
            var kind = AcquisitionSelectionLogic.GetKind(target.MediaTypeId, target.MediaSubtypeId);
            var episodes = kind is AcquisitionKind.Tv or AcquisitionKind.Anime
                ? EpisodeNumbering.Episodes(await context.ExecuteBatchAsync((ctx, token) => ctx.ReadUnitStatesAsync(target.WantedItemId, token), ct))
                : [];
            var (parent, start, end) = AcquisitionSelectionLogic.ReadCoverage(kind, offer.Title, episodes);
            var grab = new AcquisitionGrabData(target.WantedItemId, manual ? null : offer.ConnectionId, Limit(offer.Identity, 600), Limit(offer.Title, 1000), qualityKey, client.Id, parent, start, end,
                requestedByAccountId: accountId) { IndexerFlags = [.. offer.IndexerFlags], RequiresActiveMonitoring = automatic };
            var operationId = await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                if (nativePaths is not null) await ctx.RequireDownloadStorageSettingsAsync(nativePaths.Settings, token);
                return await ctx.ReserveDownloadAsync(grab, token);
            }, ct);

            // The best client by priority receives the release; Jularr's own downloader keeps the NZB in staging.
            string? failure = null;
            string? externalId = null;
            NewznabException? rateLimited = null;
            if (client.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet)
            {
                var staging = await nativePaths!.PrepareIncompleteAsync(operationId, ct, target.MediaTypeId, target.MediaSubtypeId);
                var stage = "workspace";
                try
                {
                    Directory.CreateDirectory(staging);
                    File.Delete(Path.Combine(staging, UsenetJob.NzbName));
                    stage = "transfer";
                    await _newznab.DownloadNzbAsync(offer.DownloadUri, Path.Combine(staging, UsenetJob.NzbName), ct,
                        bytes => nativePaths.ReserveWrite(operationId, staging, bytes));
                    externalId = UsenetJob.ExternalId;
                }
                catch (NewznabException exception)
                {
                    // Never log an exception: its message or inner exception can contain the signed URL.
                    _logger.LogWarning("NZB download failed for operation {OperationId}, indexer {IndexerId}: {Failure}; HTTP {StatusCode}; timeout {TimedOut}; transport {TransportError}.",
                        operationId, offer.ConnectionId, exception.Failure, (int?)exception.StatusCode, exception.InnerException is OperationCanceledException,
                        exception.InnerException is HttpRequestException transport ? transport.HttpRequestError : (HttpRequestError?)null);
                    failure = GrabFailureKey(exception);
                    rateLimited = exception.Failure == NewznabFailure.RateLimited ? exception : null;
                }
                catch (IOException exception)
                {
                    _logger.LogWarning("NZB I/O failed for operation {OperationId}, indexer {IndexerId}, stage {Stage}: {ExceptionType}; HResult {HResult}.",
                        operationId, offer.ConnectionId, stage, exception.GetType().Name, exception.HResult);
                    failure = "acquisition_unavailable";
                }
                catch (UnauthorizedAccessException exception)
                {
                    _logger.LogWarning("NZB access denied for operation {OperationId}, indexer {IndexerId}, stage {Stage}: {ExceptionType}; HResult {HResult}.",
                        operationId, offer.ConnectionId, stage, exception.GetType().Name, exception.HResult);
                    throw;
                }

                if (failure is not null && Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            else if (client.AcquisitionConnectionTypeId == AcquisitionConnectionType.Torrent)
            {
                // Jularr's own engine keeps the magnet or the checked .torrent in staging; peers are reached only later, through the VPN.
                var staging = await nativePaths!.PrepareIncompleteAsync(operationId, ct, target.MediaTypeId, target.MediaSubtypeId);
                try
                {
                    Directory.CreateDirectory(staging);
                    byte[] infoHash;
                    if (offer.DownloadUri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
                    {
                        infoHash = MagnetLink.Parse(offer.DownloadUri.OriginalString).InfoHash;
                        await File.WriteAllTextAsync(Path.Combine(staging, TorrentDownloader.MagnetName), offer.DownloadUri.OriginalString, ct);
                    }
                    else
                    {
                        var file = Path.Combine(staging, TorrentDownloader.TorrentName);
                        File.Delete(file);
                        await _newznab.DownloadNzbAsync(offer.DownloadUri, file, ct, bytes => nativePaths.ReserveWrite(operationId, staging, bytes));
                        infoHash = TorrentMetainfo.Parse(await File.ReadAllBytesAsync(file, ct)).InfoHash;
                    }

                    // A retry of the same torrent goes on with the verified pieces of the earlier try.
                    await TorrentResume.TakeOverAsync(staging, infoHash,
                        async earlierId => (await context.ExecuteBatchAsync((ctx, token) => ctx.ReadActiveDownloadsAsync(token, earlierId), ct)).Count > 0, ct);
                    externalId = TorrentJob.ExternalId;
                }
                catch (NewznabException exception)
                {
                    failure = GrabFailureKey(exception);
                    rateLimited = exception.Failure == NewznabFailure.RateLimited ? exception : null;
                }
                catch (InvalidDataException)
                {
                    failure = "acquisition_grab_failed";
                }
                catch (IOException)
                {
                    failure = "acquisition_unavailable";
                }

                if (failure is not null && Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            else if (client.AcquisitionConnectionTypeId == AcquisitionConnectionType.Qbittorrent)
            {
                // New torrents receive the same media-scoped, operation-owned folder as native jobs.
                // The client retains ownership while seeding; importing never moves its payload.
                var connection = new QbittorrentConnection(client.HttpAddress, client.Username, AcquisitionConnectionInput.Unprotect(_protector, client.ProtectedPassword));
                try
                {
                    var savePath = DownloadWorkspaceLayout.ExternalJobDirectory(client.RemotePath!, operationId, target.MediaTypeId, target.MediaSubtypeId);
                    var result = await _qbittorrent.GrabAsync(connection,
                        new QbittorrentGrabRequest(offer.DownloadUri, savePath, offer.Title), ct);
                    if (result.Success && result.Hash is { } hash)
                    {
                        externalId = hash;
                    }
                    else
                    {
                        failure = result.ReasonKey ?? "acquisition_grab_failed";
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or QbittorrentException or JsonException
                    || exception is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    failure = "acquisition_unavailable";
                }
            }
            else
            {
                SabnzbdGrabResult? result = null;
                try
                {
                    var connection = new SabnzbdConnection(client.HttpAddress, AcquisitionConnectionInput.Unprotect(_protector, client.ProtectedApiKey));
                    result = await _sabnzbd.GrabAsync(connection, new SabnzbdGrabRequest(offer.DownloadUri, offer.Title, client.DownloadCategory), ct);
                }
                catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    failure = "acquisition_unavailable";
                }
                catch (SabnzbdException)
                {
                    failure = "acquisition_grab_failed";
                }

                externalId = result is { Success: true, NzoIds.Count: > 0 } ? result.NzoIds[0] : null;
                failure ??= externalId is null ? "acquisition_grab_failed" : null;
            }

            // A refused grab releases the reservation and its indexer count; the target is searched again later.
            if (failure is not null)
            {
                if (!manual)
                {
                    await context.ExecuteBatchAsync(async (ctx, token) =>
                    {
                        await ctx.ReturnIndexerGrabAsync(new AcquisitionConnectionParameters(offer.ConnectionId), token);
                        return true;
                    }, ct);
                }

                // A rate-limited indexer rests for the wait it asked for, and the grab tries again after it.
                var retryAfter = rateLimited?.RetryAfter is { } asked && asked > TimeSpan.FromMinutes(30) ? asked : TimeSpan.FromMinutes(30);
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    if (rateLimited is not null)
                    {
                        await ctx.RecordIndexerUseAsync(new IndexerUseData(offer.ConnectionId, 0, false, rateLimited.RetryAfter is { } wait ? (int)Math.Ceiling(wait.TotalSeconds) : null), token);
                    }

                    await ctx.FailDownloadAsync(new DownloadFailureData(operationId, failure, DateTime.UtcNow + retryAfter), token);
                    return true;
                }, ct);
                throw new ServiceUnavailableException(failure);
            }

            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.StartDownloadAsync(new DownloadStartData(operationId, Limit(externalId!, 128)), token);
                return true;
            }, ct);
            _signals.Downloads.Notify();
            if (client.AcquisitionConnectionTypeId == AcquisitionConnectionType.Torrent) _signals.Torrents.Notify();
        }

        private async Task RequireClientImportSourceAsync(LogicContext context, AcquisitionConnectionData client, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(client.RemotePath) || client.DownloadStorageMountId is null && string.IsNullOrWhiteSpace(client.LocalPath)
                || client.AcquisitionConnectionTypeId == AcquisitionConnectionType.Qbittorrent
                    && client.RemotePath.TrimEnd('/', '\\').Length == 0)
            {
                throw new ServiceUnavailableException("acquisition_import_path_unconfigured");
            }

            try
            {
                if (client.DownloadStorageMountId is { } mountId)
                {
                    var mount = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStorageMountAsync(mountId, token), ct);
                    if (await _storage.Open(mount).GetAsync(client.LocalPath ?? "", ct) is not { IsDirectory: true })
                    {
                        throw new ServiceUnavailableException("acquisition_import_path_unconfigured");
                    }
                }
                else
                {
                    var directory = StorageFileSystem.ResolveDirectory(client.LocalPath!, "");
                    if (!Directory.Exists(directory))
                    {
                        throw new ServiceUnavailableException("acquisition_import_path_unconfigured");
                    }
                }
            }
            catch (Exception exception) when (exception is NasMountException or IOException or UnauthorizedAccessException)
            {
                throw new ServiceUnavailableException("acquisition_import_path_unconfigured");
            }
        }

        private async Task<bool> SearchIndexerAsync(AcquisitionConnectionData indexer, AcquisitionKind kind, int? igdbPlatform, IEnumerable<PlannedQuery> queries, SearchBudget budget,
            ConcurrentDictionary<string, ReleaseOffer> offers, IndexerUse use, bool automatic, CancellationToken ct)
        {
            // A slow indexer only costs its own share.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(budget.IndexerTimeout);
            var capabilities = ReadCapabilities(indexer);
            var categories = AcquisitionSelectionLogic.ReadCategories(kind, indexer.Categories, capabilities, igdbPlatform);
            var limit = Math.Min(PageLimit, capabilities?.MaximumLimit ?? PageLimit);
            var connection = new NewznabConnection(indexer.HttpAddress, AcquisitionConnectionInput.IndexerKey(_protector, indexer.ProtectedApiKey));
            try
            {
                foreach (var query in queries)
                {
                    // Full pages continue up to the page budget; the result budget covers the whole search.
                    for (var page = 0; page < budget.MaxPages && offers.Count < budget.MaxResults; page++)
                    {
                        // The daily query limit also holds within one search.
                        if (!AcquisitionConnectionLogic.HasQueries(indexer, use.Queries, automatic))
                        {
                            return true;
                        }

                        Interlocked.Increment(ref use.Queries);
                        var items = await _newznab.SearchAsync(connection, new NewznabQuery(query.Mode, query.Text, query.Parameters, query.AnyCategory ? [] : categories, limit, page * limit), timeout.Token);
                        foreach (var item in items)
                        {
                            var offer = new ReleaseOffer(item.Title, indexer.Id, indexer.Name, indexer.Priority, item.Guid, item.DownloadUri, item.InfoUrl, item.SizeBytes, item.PublishedAt, [query.Stage])
                                { IndexerFlags = item.IndexerFlags };
                            offers.AddOrUpdate(offer.Identity, offer, (_, existing) => AcquisitionSelectionLogic.Merge(existing, offer));
                        }

                        if (items.Count < limit)
                        {
                            break;
                        }
                    }
                }

                return true;
            }
            catch (NewznabException exception)
            {
                // The wait the indexer asked for outlasts the usual backoff.
                if (exception.RetryAfter is { } wait)
                {
                    use.RetryAfterSeconds = Math.Max(use.RetryAfterSeconds, (int)Math.Ceiling(wait.TotalSeconds));
                }

                return false;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }

        private static string GrabFailureKey(NewznabException exception) => exception.Failure switch
        {
            NewznabFailure.Unavailable => "acquisition_unavailable",
            NewznabFailure.RateLimited => "acquisition_rate_limited",
            NewznabFailure.NotFound => "acquisition_file_missing",
            _ => "acquisition_grab_failed"
        };

        private sealed class IndexerUse
        {
            public int Queries;
            public int RetryAfterSeconds;
            public bool Failed;
        }

        private static NewznabCapabilities? ReadCapabilities(AcquisitionConnectionData indexer) =>
            indexer.Capabilities is null ? null : JsonSerializer.Deserialize<NewznabCapabilities>(indexer.Capabilities);

        private static string CacheKey(long wantedItemId) => "acquisition.offers." + wantedItemId.ToString(CultureInfo.InvariantCulture);

        private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
    }
}
