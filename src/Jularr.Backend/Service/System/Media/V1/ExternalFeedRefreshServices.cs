using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Media.V1
{
    // Persistent public lists are renewed here; their readers do not decide when they expire.
    public sealed class RefreshDiscoverFeedsV1 : SystemService<RefreshDiscoverFeedsParametersV1, NoData>
    {
        private readonly ExternalFeedCache _feeds;
        private readonly WorkMetadata _metadata;
        private readonly MetadataCatalog _catalog;
        private readonly TimeProvider _time;

        internal RefreshDiscoverFeedsV1(ExternalFeedCache feeds, WorkMetadata metadata, MetadataCatalog catalog, TimeProvider time)
        {
            _feeds = feeds;
            _metadata = metadata;
            _catalog = catalog;
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(RefreshDiscoverFeedsParametersV1 parameters) => ModuleRequirement.All("Library", "DiscoverCache");
        protected override ServicePermission GetPermission(RefreshDiscoverFeedsParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var defaults = DiscoverScopes.All.SelectMany(scope => scope.Feeds.Select(feed =>
                new ExternalFeedData(DiscoverScopes.Namespace(scope), scope.MediaTypeId, scope.MediaSubtypeId, feed, 1, null, null))).ToArray();
            var pending = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var whole = await ctx.ReadWholeExternalWorkCatalogsAsync(ct);
                var selection = new ExternalFeedRefreshData(_time.GetUtcNow().UtcDateTime, 10, defaults, whole);
                return (Missing: await ctx.ReadMissingExternalFeedsAsync(selection, ct), Due: await ctx.ReadDueExternalFeedsAsync(selection, ct));
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
            if (failed)
            {
                throw new ServiceUnavailableException("metadata_unavailable");
            }
            return new UpdateResult(changed);
        }
    }
}
