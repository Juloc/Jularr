using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Integrations.Data.Newznab;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Sabnzbd;
using Microsoft.AspNetCore.DataProtection;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Admin.Wanted.V1
{
    // Indexer and client I/O of one wanted Work; the decisions belong to AcquisitionSelectionLogic.
    internal sealed class WantedAcquisition
    {
        private const int PageLimit = 100;
        private static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(30);

        private readonly NewznabClient _newznab;
        private readonly SabnzbdClient _sabnzbd;
        private readonly DirectSources _direct;
        private readonly IDataProtector _protector;
        private readonly IMemoryCache _offers;
        private readonly StorageOptions _storage;

        public WantedAcquisition(NewznabClient newznab, SabnzbdClient sabnzbd, DirectSources direct, IDataProtectionProvider protection, IMemoryCache offers, IOptions<StorageOptions> storage)
        {
            _newznab = newznab;
            _sabnzbd = sabnzbd;
            _direct = direct;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
            _offers = offers;
            _storage = storage.Value;
        }

        // Where Jularr's own downloads wait until the import.
        public static string Staging(StorageOptions storage, long operationId) => Path.Combine(storage.DirectDownloadPath, operationId.ToString(CultureInfo.InvariantCulture));

        public async Task<WantedSearchResult> SearchAsync(WantedTargetData target, IReadOnlyList<AcquisitionConnectionData> indexers, SearchDepth depth, CancellationToken ct, UnitScope? scope = null)
        {
            // Only supported media types with a profile can be ranked.
            if (AcquisitionSelectionLogic.GetKind(target.MediaTypeId) is not { } kind || target.Definition is null)
            {
                throw new DataValidationException(nameof(WantedItemParameters.WantedItemId), "invalid_transition");
            }

            var profile = AcquisitionSelectionLogic.ReadProfile(target);
            var intent = AcquisitionSelectionLogic.ReadIntent(target, kind, scope);
            var budget = SearchBudget.For(depth);
            var offers = new ConcurrentDictionary<string, ReleaseOffer>(StringComparer.Ordinal);
            var failures = 0;
            var newznab = indexers.Where(indexer => indexer.AcquisitionConnectionTypeId == AcquisitionConnectionType.Newznab).ToArray();
            var catalogs = kind is AcquisitionKind.Book or AcquisitionKind.LightNovel ? indexers.Where(indexer => DirectSources.IsDirect(indexer.AcquisitionConnectionTypeId)).ToArray() : [];
            var plans = newznab.ToDictionary(indexer => indexer.Id, indexer => SearchPlanner.Plan(intent, ReadCapabilities(indexer), depth));

            // Direct book sources answer one title and author query.
            foreach (var catalog in catalogs)
            {
                if (await _direct.SearchAsync(catalog, target, ct) is not { } found)
                {
                    failures++;
                    continue;
                }

                foreach (var offer in found.Take(budget.MaxResults))
                {
                    offers.TryAdd(offer.Identity, offer);
                }
            }

            var selection = AcquisitionSelectionLogic.Select(profile, target, kind, scope, offers.Values);

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
                    if (!await SearchIndexerAsync(indexer, kind, plans[indexer.Id].Where(query => query.Tier == current), budget, offers, token))
                    {
                        Interlocked.Increment(ref failures);
                    }
                });

                selection = AcquisitionSelectionLogic.Select(profile, target, kind, scope, offers.Values);
                if (selection.Ranked.Count(evaluation => evaluation.IsSelectable) >= budget.TargetUsable)
                {
                    break;
                }
            }

            // Manual grabs pick from what was shown; the cache is bounded by offer count.
            var grabbable = selection.Ranked.Where(evaluation => evaluation.Decision != SelectionDecision.Rejected).Select(evaluation => evaluation.Candidate.Id).ToHashSet(StringComparer.Ordinal);
            var cached = offers.Where(pair => grabbable.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal);
            _offers.Set(CacheKey(target.WantedItemId), cached, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = OfferLifetime, Size = Math.Max(1, cached.Count) });
            var sources = catalogs.Length + newznab.Length;
            var unavailable = sources > 0 && failures >= sources && offers.IsEmpty;
            return new WantedSearchResult(selection, offers, unavailable, AcquisitionSelectionLogic.OutcomeKey(selection.Outcome, unavailable));
        }

        public ReleaseOffer? FindOffer(long wantedItemId, string candidateId) =>
            _offers.TryGetValue(CacheKey(wantedItemId), out Dictionary<string, ReleaseOffer>? offers) && offers!.TryGetValue(candidateId, out var offer) ? offer : null;

        public async Task GrabAsync(LogicContext context, WantedTargetData target, ReleaseOffer offer, string? qualityKey, IReadOnlyList<AcquisitionConnectionData> clients, CancellationToken ct)
        {
            // Catalog books are downloaded by Jularr itself; the monitor takes the queued download.
            if (DirectSources.IsDirectOffer(offer.Guid) && offer.DownloadUri is { } source)
            {
                var direct = new AcquisitionGrabData(target.WantedItemId, offer.ConnectionId, Limit(offer.Identity, 600), Limit(offer.Title, 1000), qualityKey, offer.ConnectionId, null, null, null,
                    source.ToString());
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    var reserved = await ctx.ReserveDownloadAsync(direct, token);
                    await ctx.StartDownloadAsync(new DownloadStartData(reserved, Limit(offer.Guid!, 128)), token);
                    return true;
                }, ct);
                return;
            }

            if (clients.Count == 0)
            {
                throw new ServiceUnavailableException("acquisition_no_download_client");
            }

            if (offer.DownloadUri is null)
            {
                throw new DataValidationException("CandidateId", "invalid_transition");
            }

            // Reserve first so concurrent grabs cannot start a second download.
            var client = clients[0];
            var (parent, start, end) = AcquisitionSelectionLogic.ReadCoverage(target.MediaTypeId, offer.Title);
            var grab = new AcquisitionGrabData(target.WantedItemId, offer.ConnectionId, Limit(offer.Identity, 600), Limit(offer.Title, 1000), qualityKey, client.Id, parent, start, end);
            var operationId = await context.ExecuteBatchAsync((ctx, token) => ctx.ReserveDownloadAsync(grab, token), ct);

            // The best client by priority receives the release; Jularr's own downloader keeps the NZB in staging.
            string? failure = null;
            string? externalId = null;
            if (client.AcquisitionConnectionTypeId == AcquisitionConnectionType.Usenet)
            {
                var staging = Staging(_storage, operationId);
                try
                {
                    Directory.CreateDirectory(staging);
                    File.Delete(Path.Combine(staging, UsenetJob.NzbName));
                    await _newznab.DownloadNzbAsync(offer.DownloadUri, Path.Combine(staging, UsenetJob.NzbName), ct);
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
            else
            {
                SabnzbdGrabResult? result = null;
                try
                {
                    var connection = new SabnzbdConnection(client.BaseUrl, AcquisitionConnectionInput.Unprotect(_protector, client.ProtectedApiKey));
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

            // A refused grab releases the reservation; the target is searched again later.
            if (failure is not null)
            {
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
        }

        private async Task<bool> SearchIndexerAsync(AcquisitionConnectionData indexer, AcquisitionKind kind, IEnumerable<PlannedQuery> queries, SearchBudget budget,
            ConcurrentDictionary<string, ReleaseOffer> offers, CancellationToken ct)
        {
            // A slow indexer only costs its own share.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(budget.IndexerTimeout);
            var capabilities = ReadCapabilities(indexer);
            var categories = AcquisitionSelectionLogic.ReadCategories(kind, indexer.Categories, capabilities);
            var limit = Math.Min(PageLimit, capabilities?.MaximumLimit ?? PageLimit);
            var connection = new NewznabConnection(indexer.BaseUrl, AcquisitionConnectionInput.Unprotect(_protector, indexer.ProtectedApiKey));
            try
            {
                foreach (var query in queries)
                {
                    // Full pages continue up to the page budget; the result budget covers the whole search.
                    for (var page = 0; page < budget.MaxPages && offers.Count < budget.MaxResults; page++)
                    {
                        var items = await _newznab.SearchAsync(connection, new NewznabQuery(query.Mode, query.Text, query.Parameters, query.AnyCategory ? [] : categories, limit, page * limit), timeout.Token);
                        foreach (var item in items)
                        {
                            var offer = new ReleaseOffer(item.Title, indexer.Id, indexer.Name, indexer.Priority, item.Guid, item.DownloadUri, item.InfoUrl, item.SizeBytes, item.PublishedAt, [query.Stage]);
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

        private static NewznabCapabilities? ReadCapabilities(AcquisitionConnectionData indexer) =>
            indexer.Capabilities is null ? null : JsonSerializer.Deserialize<NewznabCapabilities>(indexer.Capabilities);

        private static string CacheKey(long wantedItemId) => "acquisition.offers." + wantedItemId.ToString(CultureInfo.InvariantCulture);

        private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
    }
}
