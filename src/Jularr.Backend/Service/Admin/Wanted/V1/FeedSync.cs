using Jularr.Backend.Data.Common;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Integrations.Data.Newznab;
using Jularr.Integrations.Logic.Newznab;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Admin.Wanted.V1
{
    // The recent feed (RSS) of the Newznab indexers, as Sonarr's RSS sync: one request per indexer asks for the newest releases of every media type it serves with a feed.
    // A new release about a wanted title is judged by that title's selection, as a search's release would be, and the best one is grabbed at once; no search follows.
    internal sealed class FeedSync
    {
        private const int PageLimit = 100;
        private const int MaximumPages = 3;
        private const int TargetBatch = 500;

        internal static readonly TimeSpan Retention = TimeSpan.FromDays(14);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

        private readonly NewznabClient _newznab;
        private readonly WantedAcquisition _acquisition;
        private readonly IDataProtector _protector;
        private readonly TimeProvider _time;
        private readonly ILogger<FeedSync> _logger;

        public FeedSync(NewznabClient newznab, WantedAcquisition acquisition, IDataProtectionProvider protection, TimeProvider time, ILogger<FeedSync> logger)
        {
            _newznab = newznab;
            _acquisition = acquisition;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
            _time = time;
            _logger = logger;
        }

        // Returns how many releases were grabbed.
        public async Task<int> RunAsync(LogicContext context, CancellationToken ct)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var (connections, clients) = await context.ExecuteBatchAsync(async (ctx, token) =>
                (await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Search, token), await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Download, token)), ct);
            var found = new Dictionary<long, List<ReleaseOffer>>();
            IReadOnlyList<FeedTargetData>? targets = null;
            foreach (var indexer in connections)
            {
                // The daily limits and the backoff count as for a search; the feed is the indexer's own flag, apart from its automatic searches.
                var types = AcquisitionConnectionLogic.FeedMediaTypes.Where(type => AcquisitionConnectionLogic.HasFeed(indexer, type)).ToArray();
                if (types.Length == 0 || !AcquisitionConnectionLogic.CanReadFeed(indexer, now) || ReadCategories(indexer, types) is not { Length: > 0 } categories)
                {
                    continue;
                }

                var read = await ReadFeedAsync(context, indexer, categories, ct);
                if (read.Fresh.Count == 0 || read.IsFirstRead)
                {
                    continue;
                }

                targets ??= await ReadTargetsAsync(context, now, ct);
                foreach (var item in read.Fresh.Where(item => item.DownloadUri is not null))
                {
                    var entry = new FeedEntry(item.Title, item.Category, item.TvdbId, item.TmdbId, item.ImdbId);
                    foreach (var (target, viaId) in FeedMatcher.Match(entry, targets).Where(match => types.Contains(match.Target.MediaTypeId)))
                    {
                        if (!found.TryGetValue(target.WantedItemId, out var offers))
                        {
                            found[target.WantedItemId] = offers = [];
                        }

                        offers.Add(new ReleaseOffer(item.Title, indexer.Id, indexer.Name, indexer.Priority, item.Guid, item.DownloadUri, item.InfoUrl, item.SizeBytes, item.PublishedAt,
                            viaId ? ["feed", "id"] : ["feed"]) { IndexerFlags = item.IndexerFlags });
                    }
                }
            }

            // Every indexer's releases of a title are judged together, so the best of them is taken; a title that fails does not cost the others their releases (they are seen already).
            var grabbed = 0;
            foreach (var (wantedItemId, offers) in found.OrderBy(pair => pair.Key))
            {
                IReadOnlyList<string> taken;
                try
                {
                    taken = await _acquisition.TakeFromFeedAsync(context, wantedItemId, offers, clients, ct);
                }
                catch (Exception exception) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(exception, "The feed releases of wanted target {WantedItemId} could not be judged.", wantedItemId);
                    continue;
                }

                foreach (var title in taken)
                {
                    _logger.LogInformation("The feed showed \"{Release}\"; wanted target {WantedItemId} grabbed it.", title.Length <= 200 ? title : title[..200], wantedItemId);
                }

                grabbed += taken.Count;
            }

            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.DeleteOldFeedReleasesAsync(new FeedRetentionData(now - Retention), token);
                return true;
            }, ct);
            return grabbed;
        }

        // The categories of the media types the indexer serves with its feed, within what it offers.
        private static int[] ReadCategories(AcquisitionConnectionData indexer, IEnumerable<MediaType> types)
        {
            var capabilities = WantedAcquisition.ReadCapabilities(indexer);
            return [.. types.Select(type => type switch
            {
                MediaType.Movie => AcquisitionKind.Movie,
                MediaType.Series => AcquisitionKind.Tv,
                MediaType.Music => AcquisitionKind.Music,
                _ => AcquisitionKind.Book
            }).SelectMany(kind => AcquisitionSelectionLogic.ReadCategories(kind, indexer.Categories, capabilities)).Distinct()];
        }

        // Every wanted title, whatever its next search: missing, below its cutoff or lacking a language.
        private static async Task<IReadOnlyList<FeedTargetData>> ReadTargetsAsync(LogicContext context, DateTime now, CancellationToken ct)
        {
            var ids = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadDueWantedItemsAsync(new WantedDueData(now, int.MaxValue, anyTime: true), token), ct);
            var targets = new List<FeedTargetData>();
            foreach (var batch in ids.Select(item => item.WantedItemId).Chunk(TargetBatch))
            {
                targets.AddRange(await context.ExecuteBatchAsync((ctx, token) => ctx.ReadFeedTargetsAsync(new FeedTargetQueryData(batch), token), ct));
            }

            return targets;
        }

        // The releases of the feed that were not seen before; they are stored as seen at once. The first read of an indexer only learns what is there.
        private async Task<FeedRead> ReadFeedAsync(LogicContext context, AcquisitionConnectionData indexer, int[] categories, CancellationToken ct)
        {
            var capabilities = WantedAcquisition.ReadCapabilities(indexer);
            var limit = Math.Min(PageLimit, capabilities?.MaximumLimit ?? PageLimit);
            var connection = new NewznabConnection(indexer.HttpAddress, AcquisitionConnectionInput.IndexerKey(_protector, indexer.ProtectedApiKey));
            var fresh = new List<NewznabItem>();
            var queries = 0;
            var isFirstRead = false;
            var failed = false;
            int? retryAfterSeconds = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                for (var page = 0; page < MaximumPages; page++)
                {
                    if (!AcquisitionConnectionLogic.HasQueries(indexer, queries))
                    {
                        break;
                    }

                    queries++;
                    var items = await _newznab.ReadRecentAsync(connection, categories, limit, page * limit, timeout.Token);
                    var keyed = items.Take(5 * PageLimit).GroupBy(Key).Select(group => group.First()).ToDictionary(Key);
                    var seen = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadSeenFeedReleasesAsync(new FeedSeenQueryData(indexer.Id, [.. keyed.Keys]), token), ct);
                    isFirstRead = page == 0 && !seen.HasSeen;
                    var unseen = keyed.Where(pair => !seen.ReleaseKeys.Contains(pair.Key, StringComparer.Ordinal)).ToArray();
                    if (unseen.Length > 0)
                    {
                        await context.ExecuteBatchAsync(async (ctx, token) =>
                        {
                            await ctx.RecordFeedReleasesAsync(new FeedSeenQueryData(indexer.Id, [.. unseen.Select(pair => pair.Key)]), token);
                            return true;
                        }, ct);
                        fresh.AddRange(unseen.Select(pair => pair.Value));
                    }

                    // A page that holds a release seen before has reached what the last read showed.
                    if (isFirstRead || items.Count < limit || unseen.Length < keyed.Count)
                    {
                        break;
                    }
                }
            }
            catch (NewznabException exception)
            {
                failed = true;
                retryAfterSeconds = exception.RetryAfter is { } wait ? (int)Math.Ceiling(wait.TotalSeconds) : null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failed = true;
            }

            // The request counts as one at the indexer: its limit, its backoff and its record of use.
            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.RecordIndexerUseAsync(new IndexerUseData(indexer.Id, queries, !failed, retryAfterSeconds > 0 ? retryAfterSeconds : null), token);
                return true;
            }, ct);
            return new FeedRead(fresh, isFirstRead);
        }

        // The indexer's own id of the release; the link carries the API key and is never stored.
        internal static string Key(NewznabItem item)
        {
            var key = item.Guid ?? item.Title + "|" + item.SizeBytes?.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
            return key.Length <= 600 ? key : key[..600];
        }

        private sealed record FeedRead(IReadOnlyList<NewznabItem> Fresh, bool IsFirstRead);
    }
}
