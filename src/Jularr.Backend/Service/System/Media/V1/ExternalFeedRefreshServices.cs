using System.Collections.Concurrent;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Caching.Memory;

namespace Jularr.Backend.Service.System.Media.V1
{
    // Persistent public lists are renewed here; their readers do not decide when they expire. With the instance's switch on, the titles of every Discover list
    // then get their details, within each catalog's request budget and, where the catalog waits, only while nobody uses Jularr.
    public sealed class RefreshDiscoverFeedsV1 : SystemService<RefreshDiscoverFeedsParametersV1, NoData>
    {
        // A title its catalog could not detail waits this long before a pass asks for it again; the process remembers it.
        private static readonly TimeSpan DetailRetry = TimeSpan.FromHours(6);
        private const string UndetailedKey = "discover.ahead.undetailed";

        private readonly ExternalFeedCache _feeds;
        private readonly WorkMetadata _metadata;
        private readonly MetadataCatalog _catalog;
        private readonly TimeProvider _time;
        private readonly UserActivity _activity;
        private readonly IMemoryCache _cache;

        internal RefreshDiscoverFeedsV1(ExternalFeedCache feeds, WorkMetadata metadata, MetadataCatalog catalog, TimeProvider time, UserActivity activity, IMemoryCache cache)
        {
            _feeds = feeds;
            _metadata = metadata;
            _catalog = catalog;
            _time = time;
            _activity = activity;
            _cache = cache;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(RefreshDiscoverFeedsParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(RefreshDiscoverFeedsParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var defaults = DiscoverScopes.All.SelectMany(scope => scope.Feeds.Select(feed =>
                new ExternalFeedData(DiscoverScopes.Namespace(scope), scope.MediaTypeId, scope.MediaSubtypeId, feed, 1, null, null))).ToArray();
            var pending = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var whole = await ctx.ReadWholeExternalWorkCatalogsAsync(ct);
                var selection = new ExternalFeedRefreshData(_time.GetUtcNow().UtcDateTime, 10, defaults, whole);
                return (Missing: await ctx.ReadMissingExternalFeedsAsync(selection, ct), Due: await ctx.ReadDueExternalFeedsAsync(selection, ct), Ahead: await ctx.ReadCatalogPrefetchesAsync(ct));
            }, CancellationToken, mode: SqlAccessMode.ReadOnly);
            var changed = false;
            var failed = false;
            foreach (var feed in pending.Missing.Concat(pending.Due))
            {
                CancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _feeds.LoadAsync(Context, feed, (entry, ct) => entry.Namespace is "tmdb_movie" or "tmdb_tv"
                        ? _metadata.FetchExternalFeedAsync(Context, entry, ct)
                        : _catalog.FetchExternalFeedAsync(entry, ct), CancellationToken, refresh: true);
                    changed = true;
                }
                catch (ServiceUnavailableException)
                {
                    // Leave the last good list intact, and let other keys run before this one is retried.
                    await Context.ExecuteBatchAsync(async (ctx, ct) =>
                    {
                        await ctx.PostponeExternalFeedAsync(new ExternalFeedRetryData(feed, _time.GetUtcNow().UtcDateTime.AddMinutes(1)), ct);
                        return true;
                    }, CancellationToken);
                    failed = true;
                }
            }

            if (await DetailAheadAsync(pending.Ahead) > 0)
            {
                changed = true;
            }

            if (failed)
            {
                throw new ServiceUnavailableException("metadata_unavailable");
            }
            return new UpdateResult(changed);
        }

        // Each catalog details up to its requests a minute of its Discover lists' titles through the one detail path; a catalog that cannot answer ends its part.
        private async Task<int> DetailAheadAsync(IReadOnlyList<CatalogPrefetchData> catalogs)
        {
            var now = _time.GetUtcNow();
            var undetailed = _cache.GetOrCreate(UndetailedKey, entry =>
            {
                entry.Size = 1;
                return new ConcurrentDictionary<long, DateTimeOffset>();
            })!;
            foreach (var (workId, retryAt) in undetailed)
            {
                if (retryAt <= now) undetailed.TryRemove(workId, out _);
            }

            var isUsed = _activity.IsUsedWithin(ExternalWorkLogic.UseLasts);
            var detailed = 0;
            foreach (var catalog in catalogs.Where(catalog => !(catalog.PausesWhileUsed && isUsed)))
            {
                var query = new CatalogPrefetchQueryData(catalog.Namespace, catalog.RequestsPerMinute, [.. undetailed.Keys]);
                var works = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadUndetailedDiscoverWorksAsync(query, ct), CancellationToken, mode: SqlAccessMode.ReadOnly);
                foreach (var work in works)
                {
                    CancellationToken.ThrowIfCancellationRequested();
                    if (!await _catalog.DetailAsync(Context, work.Id, null, CancellationToken))
                    {
                        undetailed[work.Id] = now + DetailRetry;
                        break;
                    }

                    detailed++;
                }
            }

            return detailed;
        }
    }
}
