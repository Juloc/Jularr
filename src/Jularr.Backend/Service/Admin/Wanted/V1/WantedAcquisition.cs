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
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Data.Qbittorrent;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Sabnzbd;
using Jularr.Integrations.Logic.Qbittorrent;
using Jularr.Integrations.Logic.Storage;
using Microsoft.AspNetCore.DataProtection;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Logic.Storage;
using Microsoft.Extensions.Caching.Memory;

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

        public WantedAcquisition(NewznabClient newznab, SabnzbdClient sabnzbd, QbittorrentClient qbittorrent, DirectSources direct, NyaaSources nyaa,
            IDataProtectionProvider protection, IMemoryCache offers, TimeProvider time, DownloadStorage downloads, StorageAccess storage, WorkSignals signals)
        {
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
            var allNewznab = indexers.Where(indexer => indexer.AcquisitionConnectionTypeId == AcquisitionConnectionType.Newznab).ToArray();
            var newznab = allNewznab.Where(indexer => AcquisitionConnectionLogic.CanSearch(indexer, target.MediaTypeId, automatic, now)).ToArray();
            var skipped = allNewznab.Count(indexer => AcquisitionConnectionLogic.Serves(indexer, target.MediaTypeId, automatic)) - newznab.Length;
            var uses = new ConcurrentDictionary<long, IndexerUse>();
            var catalogs = kind is AcquisitionKind.Book or AcquisitionKind.LightNovel ? indexers.Where(indexer => DirectSources.IsDirect(indexer.AcquisitionConnectionTypeId)).ToArray() : [];
            var torrents = kind is AcquisitionKind.LightNovel ? indexers.Where(indexer => indexer.AcquisitionConnectionTypeId == AcquisitionConnectionType.Nyaa).ToArray() : [];
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
                        if (await _nyaa.SearchAsync(source, wanted, inner) is not { } answers)
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

            // The direct sources answer at the same time as the indexers; each of them on its own.
            async Task SearchDirectSourcesAsync(IReadOnlyList<AcquisitionConnectionData> sources, WantedTargetData wanted, SearchBudget limits,
                ConcurrentDictionary<string, ReleaseOffer> found, CancellationToken token)
            {
                await Parallel.ForEachAsync(sources, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, sources.Count), CancellationToken = token },
                    async (source, inner) =>
                    {
                        if (await _direct.SearchAsync(source, wanted, inner, Enough) is not { } answers)
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
                return AcquisitionSelectionLogic.Select(profile, target, kind, scope, all, focusLanguage).Ranked.Any(evaluation => evaluation.IsSelectable);
            }

            var selection = AcquisitionSelectionLogic.Select(profile, target, kind, scope, offers.Values, focusLanguage);

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
                    if (!await SearchIndexerAsync(indexer, kind, target.PlatformIgdbId, plans[indexer.Id].Where(query => query.Tier == current), budget, offers, use, token))
                    {
                        use.Failed = true;
                        Interlocked.Increment(ref failures);
                    }
                });

                // The direct sources answered at the same time; their releases belong to the choice.
                await direct;
                await nyaaSearch;
                selection = AcquisitionSelectionLogic.Select(profile, target, kind, scope, offers.Values, focusLanguage);
                if (selection.Ranked.Count(evaluation => evaluation.IsSelectable) >= budget.TargetUsable)
                {
                    break;
                }
            }

            // The direct sources are waited for; they answered at the same time as the indexers.
            await direct;
            await nyaaSearch;
            selection = AcquisitionSelectionLogic.Select(profile, target, kind, scope, offers.Values, focusLanguage);

            // Manual grabs pick from what was shown; the cache is bounded by offer count.
            var grabbable = selection.Ranked.Where(evaluation => evaluation.Decision != SelectionDecision.Rejected).Select(evaluation => evaluation.Candidate.Id).ToHashSet(StringComparer.Ordinal);
            var cached = offers.Where(pair => grabbable.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal);
            _offers.Set(CacheKey(target.WantedItemId), cached, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = OfferLifetime, Size = Math.Max(1, cached.Count) });
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                foreach (var (id, use) in uses)
                {
                    await ctx.RecordIndexerUseAsync(new IndexerUseData(id, use.Queries, !use.Failed), token);
                }

                return true;
            }, ct);

            // Indexers left out for backoff or limits count as unavailable, not as empty.
            var sources = catalogs.Length + torrents.Length + newznab.Length + skipped;
            var unavailable = sources > 0 && failures + skipped >= sources && offers.IsEmpty;
            return new WantedSearchResult(selection, offers, unavailable, AcquisitionSelectionLogic.OutcomeKey(selection.Outcome, unavailable));
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
                throw new ServiceUnavailableException("acquisition_unavailable");
            }

            if (result.Selection.Winner is not { } winner)
            {
                return new AutomaticSearchResultV1(result.OutcomeKey, null);
            }

            var offer = result.Offers[winner.Candidate.Id];
            await GrabAsync(context, target, offer, winner.Score?.QualityKey, clients, ct, accountId);
            return new AutomaticSearchResultV1("grabbed", TextLimit.Cut(offer.Title, 1000));
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

            // Route by transport first. A .torrent or magnet must never be handed to SABnzbd,
            // nor may an NZB be handed to qBittorrent simply because it has the highest priority.
            var isTorrent = offer.Guid?.StartsWith("nyaa:", StringComparison.OrdinalIgnoreCase) == true
                || offer.DownloadUri?.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase) == true
                || offer.DownloadUri?.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) == true;
            var compatible = clients.Where(connection => isTorrent
                ? connection.AcquisitionConnectionTypeId == AcquisitionConnectionType.Qbittorrent
                : connection.AcquisitionConnectionTypeId is AcquisitionConnectionType.Sabnzbd or AcquisitionConnectionType.Usenet);
            var client = compatible.FirstOrDefault(connection => connection.MediaTypeIds.Contains((int)target.MediaTypeId))
                ?? compatible.FirstOrDefault(connection => connection.MediaTypeIds.Length == 0);
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
            if (client.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet)
            {
                nativePaths = await _downloads.OpenAsync(context, DownloadStorageArea.Usenet, ct);
            }

            // A grab counts against its indexer's daily limit.
            if (!await context.ExecuteBatchAsync((ctx, token) => ctx.CountIndexerGrabAsync(new AcquisitionConnectionParameters(offer.ConnectionId), token), ct))
            {
                throw new ServiceUnavailableException("acquisition_limit_reached");
            }

            // Reserve first so concurrent grabs cannot start a second download.
            var kind = AcquisitionSelectionLogic.GetKind(target.MediaTypeId, target.MediaSubtypeId);
            var episodes = kind is AcquisitionKind.Tv or AcquisitionKind.Anime
                ? EpisodeNumbering.Episodes(await context.ExecuteBatchAsync((ctx, token) => ctx.ReadUnitStatesAsync(target.WantedItemId, token), ct))
                : [];
            var (parent, start, end) = AcquisitionSelectionLogic.ReadCoverage(kind, offer.Title, episodes);
            var grab = new AcquisitionGrabData(target.WantedItemId, offer.ConnectionId, Limit(offer.Identity, 600), Limit(offer.Title, 1000), qualityKey, client.Id, parent, start, end,
                requestedByAccountId: accountId) { IndexerFlags = [.. offer.IndexerFlags], RequiresActiveMonitoring = automatic };
            var operationId = await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                if (nativePaths is not null) await ctx.RequireDownloadStorageSettingsAsync(nativePaths.Settings, token);
                return await ctx.ReserveDownloadAsync(grab, token);
            }, ct);

            // The best client by priority receives the release; Jularr's own downloader keeps the NZB in staging.
            string? failure = null;
            string? externalId = null;
            if (client.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet)
            {
                var staging = await nativePaths!.PrepareIncompleteAsync(operationId, ct);
                try
                {
                    Directory.CreateDirectory(staging);
                    File.Delete(Path.Combine(staging, UsenetJob.NzbName));
                    await _newznab.DownloadNzbAsync(offer.DownloadUri, Path.Combine(staging, UsenetJob.NzbName), ct,
                        bytes => nativePaths.ReserveWrite(operationId, staging, bytes));
                    externalId = UsenetJob.ExternalId;
                }
                catch (NewznabException exception)
                {
                    failure = exception.Failure == NewznabFailure.Unavailable ? "acquisition_unavailable" : "acquisition_grab_failed";
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
                // The torrent client writes the file into the folder Jularr reads it from.
                var connection = new QbittorrentConnection(client.HttpAddress, client.Username, AcquisitionConnectionInput.Unprotect(_protector, client.ProtectedPassword));
                try
                {
                    var result = await _qbittorrent.GrabAsync(connection,
                        new QbittorrentGrabRequest(offer.DownloadUri, client.RemotePath ?? "", offer.Title), ct);
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
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    await ctx.ReturnIndexerGrabAsync(new AcquisitionConnectionParameters(offer.ConnectionId), token);
                    return true;
                }, ct);
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    await ctx.FailDownloadAsync(new DownloadFailureData(operationId, failure, DateTime.UtcNow.AddMinutes(30)), token);
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
        }

        private async Task RequireClientImportSourceAsync(LogicContext context, AcquisitionConnectionData client, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(client.RemotePath) || client.DownloadStorageMountId is null && string.IsNullOrWhiteSpace(client.LocalPath))
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
            ConcurrentDictionary<string, ReleaseOffer> offers, IndexerUse use, CancellationToken ct)
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
                        if (!AcquisitionConnectionLogic.HasQueries(indexer, use.Queries))
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
            catch (NewznabException)
            {
                return false;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }

        private sealed class IndexerUse
        {
            public int Queries;
            public bool Failed;
        }

        private static NewznabCapabilities? ReadCapabilities(AcquisitionConnectionData indexer) =>
            indexer.Capabilities is null ? null : JsonSerializer.Deserialize<NewznabCapabilities>(indexer.Capabilities);

        private static string CacheKey(long wantedItemId) => "acquisition.offers." + wantedItemId.ToString(CultureInfo.InvariantCulture);

        private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
    }
}
