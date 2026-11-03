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
    // The recent feed (RSS) of the Newznab indexers: one request per indexer asks for the newest movie and series releases. The feed only triggers: a release
    // that is new and about a wanted target makes that target due, and the targeted search and its selection decide, as for any search.
    internal sealed class FeedSync
    {
        private const int PageLimit = 100;
        private const int MaximumPages = 3;
        private const int TargetBatch = 500;

        // A feed shows the same title often (every release of an episode); a target is searched at most this often because of it.
        internal const int SearchGapMinutes = 60;
        internal const string OutcomeKey = "feed_release";
        internal static readonly TimeSpan Retention = TimeSpan.FromDays(14);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

        private readonly NewznabClient _newznab;
        private readonly IDataProtector _protector;
        private readonly TimeProvider _time;
        private readonly ILogger<FeedSync> _logger;

        public FeedSync(NewznabClient newznab, IDataProtectionProvider protection, TimeProvider time, ILogger<FeedSync> logger)
        {
            _newznab = newznab;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
            _time = time;
            _logger = logger;
        }

        // Returns how many wanted targets became due.
        public async Task<int> RunAsync(LogicContext context, CancellationToken ct)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var connections = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Search, token), ct);
            var due = new Dictionary<long, (string Indexer, string Release)>();
            IReadOnlyList<FeedTargetData>? targets = null;
            foreach (var indexer in connections)
            {
                // Limits and the backoff count as for a search; manual-only indexers are left out.
                var types = new[] { MediaType.Movie, MediaType.Series }.Where(type => AcquisitionConnectionLogic.HasFeed(indexer, type) && AcquisitionConnectionLogic.CanSearch(indexer, type, true, now)).ToArray();
                if (types.Length == 0 || ReadCategories(indexer, types) is not { Length: > 0 } categories)
                {
                    continue;
                }

                var read = await ReadFeedAsync(context, indexer, categories, ct);
                if (read.Fresh.Count == 0 || read.IsFirstRead)
                {
                    continue;
                }

                targets ??= await ReadTargetsAsync(context, now, ct);
                foreach (var item in read.Fresh)
                {
                    var entry = new FeedEntry(item.Title, item.Category, item.TvdbId, item.TmdbId, item.ImdbId);
                    foreach (var target in FeedMatcher.Match(entry, targets).Where(target => types.Contains(target.MediaTypeId)))
                    {
                        due.TryAdd(target.WantedItemId, (indexer.Name, item.Title));
                    }
                }
            }

            var changed = 0;
            foreach (var batch in due.Keys.Order().Chunk(TargetBatch))
            {
                var made = await context.ExecuteBatchAsync((ctx, token) => ctx.MakeWantedDueAsync(new FeedDueData(batch, OutcomeKey, now, SearchGapMinutes), token), ct);
                foreach (var target in made)
                {
                    var (indexer, release) = due[target.WantedItemId];
                    _logger.LogInformation("The feed of {Indexer} showed \"{Release}\"; wanted target {WantedItemId} is searched now.", indexer, release.Length <= 200 ? release : release[..200], target.WantedItemId);
                    changed++;
                }
            }

            await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await ctx.DeleteOldFeedReleasesAsync(new FeedRetentionData(now - Retention), token);
                return true;
            }, ct);
            return changed;
        }

        // The categories of the video the indexer serves, within what it offers.
        private static int[] ReadCategories(AcquisitionConnectionData indexer, IEnumerable<MediaType> types)
        {
            var capabilities = WantedAcquisition.ReadCapabilities(indexer);
            return [.. types.SelectMany(type => AcquisitionSelectionLogic.ReadCategories(type == MediaType.Movie ? AcquisitionKind.Movie : AcquisitionKind.Tv, indexer.Categories, capabilities)).Distinct()];
        }

        // Every target that is wanted at some time: the ones due now and the ones waiting for their search.
        private static async Task<IReadOnlyList<FeedTargetData>> ReadTargetsAsync(LogicContext context, DateTime now, CancellationToken ct)
        {
            var ids = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadDueWantedItemsAsync(new WantedDueData(now.AddYears(100), int.MaxValue), token), ct);
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
