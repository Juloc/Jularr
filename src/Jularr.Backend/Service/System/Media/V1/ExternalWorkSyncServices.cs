using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Tmdb;
using Jularr.Integrations.Logic.Tmdb;

namespace Jularr.Backend.Service.System.Media.V1
{
    // One pass of full local discovery for TMDB: the daily id list, the changes, then the next entries by popularity.
    // Provider reads happen between short write batches, never inside one.
    public sealed class SyncExternalWorksV1 : SystemService<SyncExternalWorksParametersV1, NoData>
    {
        private readonly WorkMetadata _metadata;
        private readonly TimeProvider _time;
        private readonly UserActivity _activity;

        internal SyncExternalWorksV1(WorkMetadata metadata, TimeProvider time, UserActivity activity)
        {
            _metadata = metadata;
            _time = time;
            _activity = activity;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(SyncExternalWorksParametersV1 parameters) => ModuleRequirement.All("Library", "LocalDiscovery");
        protected override ServicePermission GetPermission(SyncExternalWorksParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Without a TMDB key there is nothing to keep.
            TmdbConnection connection;
            try
            {
                connection = await _metadata.ConnectAsync(Context, CancellationToken);
            }
            catch (ServiceUnavailableException)
            {
                return new UpdateResult(false);
            }

            var windows = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadExternalWorkSyncWindowsAsync(ct), CancellationToken);
            var isUsed = _activity.IsUsedWithin(ExternalWorkLogic.UseLasts);
            var changed = 0;
            foreach (var mediaType in new[] { MediaType.Movie, MediaType.Series })
            {
                var list = new ExternalWorkListData(WorkMetadata.Namespace(mediaType), mediaType, null);
                var now = _time.GetUtcNow();
                var sync = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadExternalWorkSyncAsync(list.Namespace, ct), CancellationToken);
                if (sync is null || !ExternalWorkLogic.MaySync(sync, windows, TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _time.LocalTimeZone).DateTime), isUsed)) continue;

                var today = DateOnly.FromDateTime(now.UtcDateTime);
                var listedOn = sync.ListedOn;
                if (ExternalWorkLogic.IsListDue(sync, now) && await ListAsync(mediaType, list, today))
                {
                    listedOn = today;
                    await Context.ExecuteBatchAsync((ctx, ct) => ctx.SaveExternalWorkSyncAsync(new ExternalWorkSyncProgressData(list.Namespace, listedOn, sync.Cursor, sync.ChangesReadAt), ct),
                        CancellationToken);
                }

                if (ExternalWorkLogic.AreChangesDue(sync, now))
                {
                    // The first pass has every entry still to read; later ones read what changed since.
                    if (sync.ChangesReadAt is { } read) changed += await MarkChangesAsync(connection, mediaType, list, DateOnly.FromDateTime(read), today);
                    await Context.ExecuteBatchAsync((ctx, ct) => ctx.SaveExternalWorkSyncAsync(new ExternalWorkSyncProgressData(list.Namespace, listedOn, sync.Cursor, now.UtcDateTime), ct),
                        CancellationToken);
                }

                // A pass runs every minute: the catalog's requests a minute are its budget.
                changed += await DetailAsync(connection, mediaType, list, sync.RequestsPerMinute);
            }

            return new UpdateResult(changed > 0);
        }

        // Adult titles and video extras stay out of the catalog.
        private Task<bool> ListAsync(MediaType mediaType, ExternalWorkListData list, DateOnly day) =>
            _metadata.ReadIdListAsync(mediaType, day, async entries =>
            {
                var rows = JsonSerializer.Serialize(entries.Where(entry => !entry.IsAdult && !entry.IsVideo)
                    .Select(entry => new { Value = entry.Id.ToString(CultureInfo.InvariantCulture), Title = entry.OriginalTitle, entry.Popularity }));
                await Context.ExecuteBatchAsync((ctx, ct) => ctx.ListExternalWorksAsync(new ExternalWorkEntriesData(list, rows), ct), CancellationToken);
            }, CancellationToken);

        private async Task<int> MarkChangesAsync(TmdbConnection connection, MediaType mediaType, ExternalWorkListData list, DateOnly from, DateOnly to)
        {
            // TMDB answers at most fourteen days at once; an older pass starts the window two weeks back.
            if (to.DayNumber - from.DayNumber > 14) from = to.AddDays(-14);
            var changed = 0;
            for (int page = 1, pages = 1; page <= pages; page++)
            {
                var (ids, total) = await _metadata.ReadChangesAsync(connection, mediaType, from, to, page, CancellationToken);
                pages = Math.Min(total, 500);
                var values = new ExternalWorkValuesData(list.Namespace, [.. ids.Select(id => id.ToString(CultureInfo.InvariantCulture))]);
                changed += await Context.ExecuteBatchAsync((ctx, ct) => ctx.MarkChangedExternalWorksAsync(values, ct), CancellationToken);
            }

            return changed;
        }

        private async Task<int> DetailAsync(TmdbConnection connection, MediaType mediaType, ExternalWorkListData list, int limit)
        {
            var values = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadUndetailedExternalWorksAsync(new ExternalWorkQueueData(list.Namespace, limit), ct), CancellationToken);
            if (values.Count == 0) return 0;

            // A few reads at a time; a provider limit ends the pass and the next one goes on.
            var entries = new List<TmdbCatalogEntry>();
            var gone = new List<string>();
            try
            {
                foreach (var chunk in values.Chunk(4))
                {
                    var read = await Task.WhenAll(chunk.Select(async value => (value, entry: await _metadata.ReadCatalogEntryAsync(connection, mediaType, int.Parse(value, CultureInfo.InvariantCulture), CancellationToken))));
                    foreach (var (value, entry) in read)
                    {
                        if (entry is null) gone.Add(value);
                        else entries.Add(entry);
                    }
                }
            }
            catch (ServiceUnavailableException) when (entries.Count + gone.Count > 0)
            {
            }

            var rows = JsonSerializer.Serialize(entries.Select(entry => new
            {
                Value = entry.Id.ToString(CultureInfo.InvariantCulture),
                entry.Title,
                entry.OriginalTitle,
                ReleasedOn = entry.ReleasedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                entry.Overview,
                ImageUrl = TmdbClient.PosterUrl(entry.PosterPath),
                BackdropUrl = TmdbClient.BackdropUrl(entry.BackdropPath),
                entry.Popularity,
                Rating = entry.VoteAverage is > 0 ? entry.VoteAverage : null,
                entry.ImdbId,
                entry.IsAdult
            }));
            return await Context.ExecuteBatchAsync((ctx, ct) => ctx.DetailExternalWorksAsync(new ExternalWorkEntriesData(list, rows, [.. gone]), ct), CancellationToken);
        }
    }
}
