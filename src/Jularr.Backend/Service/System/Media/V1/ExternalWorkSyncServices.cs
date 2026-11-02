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
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Data.Tmdb;
using Jularr.Integrations.Logic.Tmdb;

namespace Jularr.Backend.Service.System.Media.V1
{
    // One pass of full local discovery for TMDB films and AniList: TMDB's daily id list, the changes, then the next entries by popularity.
    // Provider reads happen between short write batches, never inside one.
    public sealed class SyncExternalWorksV1 : SystemService<SyncExternalWorksParametersV1, NoData>
    {
        private readonly WorkMetadata _metadata;
        private readonly AniListMetadata _aniList;
        private readonly TimeProvider _time;
        private readonly UserActivity _activity;

        internal SyncExternalWorksV1(WorkMetadata metadata, AniListMetadata aniList, TimeProvider time, UserActivity activity)
        {
            _metadata = metadata;
            _aniList = aniList;
            _time = time;
            _activity = activity;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(SyncExternalWorksParametersV1 parameters) => ModuleRequirement.All("Library", "DiscoverCache", "LocalDiscovery");
        protected override ServicePermission GetPermission(SyncExternalWorksParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var (tier, windows) = await Context.ExecuteBatchAsync(async (ctx, ct) => (await ctx.ReadCachingTierAsync(ct), await ctx.ReadExternalWorkSyncWindowsAsync(ct)), CancellationToken);
            var isUsed = _activity.IsUsedWithin(ExternalWorkLogic.UseLasts);
            var changed = await SyncAniListAsync(tier, windows, isUsed);

            // Without a TMDB key there is nothing to keep from TMDB.
            TmdbConnection connection;
            try
            {
                connection = await _metadata.ConnectAsync(Context, CancellationToken);
            }
            catch (ServiceUnavailableException)
            {
                return new UpdateResult(changed > 0);
            }

            // Series come from TheTVDB, which has no full list; only films are kept whole from TMDB.
            var list = new ExternalWorkListData(WorkMetadata.Namespace(MediaType.Movie), MediaType.Movie, null);
            var now = _time.GetUtcNow();
            var sync = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadExternalWorkSyncAsync(list.Namespace, ct), CancellationToken);
            if (sync is null || !ExternalWorkLogic.MaySync(tier, sync, windows, TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _time.LocalTimeZone).DateTime), isUsed))
            {
                return new UpdateResult(changed > 0);
            }

            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var listedOn = sync.ListedOn;
            if (ExternalWorkLogic.IsListDue(sync, now) && await ListAsync(list, today))
            {
                listedOn = today;
                await Context.ExecuteBatchAsync((ctx, ct) => ctx.SaveExternalWorkSyncAsync(new ExternalWorkSyncProgressData(list.Namespace, listedOn, sync.Cursor, sync.ChangesReadAt), ct),
                    CancellationToken);
            }

            if (ExternalWorkLogic.AreChangesDue(sync, now))
            {
                // The first pass has every entry still to read; later ones read what changed since.
                if (sync.ChangesReadAt is { } read) changed += await MarkChangesAsync(connection, list, DateOnly.FromDateTime(read), today);
                await Context.ExecuteBatchAsync((ctx, ct) => ctx.SaveExternalWorkSyncAsync(new ExternalWorkSyncProgressData(list.Namespace, listedOn, sync.Cursor, now.UtcDateTime), ct),
                    CancellationToken);
            }

            // A pass runs every minute: the catalog's requests a minute are its budget; once the listed titles are all read the catalog is whole.
            var (detailed, isDone) = await DetailAsync(connection, list, sync.RequestsPerMinute);
            changed += detailed;
            if (isDone && listedOn is not null) await Context.ExecuteBatchAsync((ctx, ct) => ctx.CompleteExternalWorkSyncAsync(list.Namespace, ct), CancellationToken);
            return new UpdateResult(changed > 0);
        }

        // AniList has no id list: a pass walks its titles by id, a page per request within the budget, and goes on where the last one stopped.
        private async Task<int> SyncAniListAsync(CachingTier tier, IReadOnlyList<ExternalWorkSyncWindowData> windows, bool isUsed)
        {
            var now = _time.GetUtcNow();
            var sync = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadExternalWorkSyncAsync(AniListMetadata.Namespace, ct), CancellationToken);
            if (sync is null || !ExternalWorkLogic.MaySync(tier, sync, windows, TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _time.LocalTimeZone).DateTime), isUsed)
                || !ExternalWorkLogic.IsFullPassDue(sync, now))
            {
                return 0;
            }

            var cursor = sync.Cursor ?? 0;
            var complete = false;
            var changed = 0;
            for (var request = 0; request < sync.RequestsPerMinute; request++)
            {
                IReadOnlyList<AniListCatalogEntry> entries;
                bool more;
                try
                {
                    (entries, more) = await _aniList.ReadCatalogPageAsync(cursor, CancellationToken);
                }
                catch (ServiceUnavailableException)
                {
                    break;
                }

                changed += await KeepAniListAsync(entries);
                if (entries.Count > 0) cursor = entries.Max(entry => entry.Id);
                if (!more || entries.Count == 0)
                {
                    complete = true;
                    break;
                }
            }

            var progress = complete ? new ExternalWorkSyncProgressData(sync.Namespace, DateOnly.FromDateTime(now.UtcDateTime), null, sync.ChangesReadAt)
                : new ExternalWorkSyncProgressData(sync.Namespace, sync.ListedOn, cursor > 0 ? cursor : null, sync.ChangesReadAt);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var saved = await ctx.SaveExternalWorkSyncAsync(progress, ct);
                return complete ? saved + await ctx.CompleteExternalWorkSyncAsync(sync.Namespace, ct) : saved;
            }, CancellationToken);
            return changed;
        }

        private async Task<int> KeepAniListAsync(IReadOnlyList<AniListCatalogEntry> entries)
        {
            var changed = 0;
            foreach (var group in entries.Select(entry => (entry, place: AniListMatching.CatalogType(entry.Type, entry.Format))).Where(item => item.place is not null).GroupBy(item => item.place!.Value))
            {
                var list = new ExternalWorkListData(AniListMetadata.Namespace, group.Key.Type, group.Key.Subtype);
                var works = group.Select(item => new ExternalWorkData(AniListMetadata.Namespace, item.entry.Id.ToString(CultureInfo.InvariantCulture), TextLimit.Cut(item.entry.Title, 500)!,
                    TextLimit.Cut(item.entry.OriginalTitle, 500), item.entry.Year is >= 1800 and <= 2200 ? item.entry.Year : null, TextLimit.Cut(item.entry.Description, 4000), null,
                    item.entry.CoverUrl, null, null)).ToArray();
                var details = JsonSerializer.Serialize(group.Select(item => new
                {
                    Value = item.entry.Id.ToString(CultureInfo.InvariantCulture),
                    item.entry.Title,
                    item.entry.OriginalTitle,
                    ReleasedOn = item.entry.ReleasedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Overview = item.entry.Description,
                    ImageUrl = item.entry.CoverUrl,
                    BackdropUrl = (string?)null,
                    Popularity = item.entry.Popularity,
                    Rating = item.entry.AverageScore is > 0 ? item.entry.AverageScore / 10.0 : null,
                    // AniList shows no vote count; how many people list a title stands for it.
                    Votes = item.entry.Popularity,
                    ImdbId = (string?)null,
                    IsAdult = false
                }));
                changed += await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    await ctx.SaveExternalWorksAsync(new ExternalWorkBatchData(list, works, false), ct);
                    return await ctx.DetailExternalWorksAsync(new ExternalWorkEntriesData(list, details), ct);
                }, CancellationToken);
            }

            return changed;
        }

        // Adult titles and video extras stay out of the catalog.
        private Task<bool> ListAsync(ExternalWorkListData list, DateOnly day) =>
            _metadata.ReadMovieIdListAsync(day, async entries =>
            {
                var rows = JsonSerializer.Serialize(entries.Where(entry => !entry.IsAdult && !entry.IsVideo)
                    .Select(entry => new { Value = entry.Id.ToString(CultureInfo.InvariantCulture), Title = entry.OriginalTitle, entry.Popularity }));
                await Context.ExecuteBatchAsync((ctx, ct) => ctx.ListExternalWorksAsync(new ExternalWorkEntriesData(list, rows), ct), CancellationToken);
            }, CancellationToken);

        private async Task<int> MarkChangesAsync(TmdbConnection connection, ExternalWorkListData list, DateOnly from, DateOnly to)
        {
            // TMDB answers at most fourteen days at once; an older pass starts the window two weeks back.
            if (to.DayNumber - from.DayNumber > 14) from = to.AddDays(-14);
            var changed = 0;
            for (int page = 1, pages = 1; page <= pages; page++)
            {
                var (ids, total) = await _metadata.ReadMovieChangesAsync(connection, from, to, page, CancellationToken);
                pages = Math.Min(total, 500);
                var values = new ExternalWorkValuesData(list.Namespace, [.. ids.Select(id => id.ToString(CultureInfo.InvariantCulture))]);
                changed += await Context.ExecuteBatchAsync((ctx, ct) => ctx.MarkChangedExternalWorksAsync(values, ct), CancellationToken);
            }

            return changed;
        }

        private async Task<(int Changed, bool IsDone)> DetailAsync(TmdbConnection connection, ExternalWorkListData list, int limit)
        {
            var values = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadUndetailedExternalWorksAsync(new ExternalWorkQueueData(list.Namespace, limit), ct), CancellationToken);
            if (values.Count == 0) return (0, true);

            // A few reads at a time; a provider limit ends the pass and the next one goes on.
            var entries = new List<TmdbCatalogEntry>();
            var gone = new List<string>();
            try
            {
                foreach (var chunk in values.Chunk(4))
                {
                    var read = await Task.WhenAll(chunk.Select(async value => (value, entry: await _metadata.ReadCatalogEntryAsync(connection, MediaType.Movie, int.Parse(value, CultureInfo.InvariantCulture), CancellationToken))));
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
                Votes = entry.VoteCount,
                entry.ImdbId,
                entry.IsAdult
            }));
            return (await Context.ExecuteBatchAsync((ctx, ct) => ctx.DetailExternalWorksAsync(new ExternalWorkEntriesData(list, rows, [.. gone]), ct), CancellationToken), false);
        }
    }
}
