using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Microsoft.Extensions.Caching.Memory;

namespace Jularr.Backend.Service.Core
{
    internal sealed class ExternalFeedCache(IMemoryCache cache) : IDisposable
    {
        private readonly SemaphoreSlim _tmdb = new(1, 1);
        private readonly SemaphoreSlim _aniList = new(1, 1);
        private readonly SemaphoreSlim _books = new(1, 1);

        // Runs inside the caller's read batch, alongside its permission checks and viewer-state projection.
        public static async Task<(ExternalFeedData Feed, IReadOnlyList<ExternalWorkData>? Items)> ReadAsync(LogicContext context, ExternalFeedData feed, CancellationToken ct)
        {
            var resolved = feed;
            if (feed.Language is null && feed.Namespace is "tmdb_movie" or "tmdb_tv")
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        locale."Locale"
                    FROM
                        "InstanceSettings" AS settings
                        JOIN "UiLocales" AS locale ON locale."Id" = settings."UiLocaleId";
                    """;
                var language = await context.ReadSql.ReadOptionalAsync<MetadataLanguageData>(sql, ct);
                resolved = new ExternalFeedData(feed.Namespace, feed.MediaTypeId, feed.MediaSubtypeId, feed.DiscoverFeedTypeId, feed.Page, feed.SourceValue, language?.Locale ?? "en");
            }
            var items = await context.ReadExternalFeedAsync(resolved, ct);
            if (items is null && resolved.DiscoverFeedTypeId == DiscoverFeedType.Trending && resolved.Page == 1 && resolved.SourceValue is null)
            {
                var previous = await context.ReadTrendingExternalWorksAsync(new ExternalWorkListData(ExternalFeedLogic.CatalogOf(resolved.Namespace), resolved.MediaTypeId, resolved.MediaSubtypeId), ct);
                if (previous.Count > 0)
                {
                    items = previous;
                }
            }
            return (resolved, items);
        }

        public async Task<IReadOnlyList<ExternalWorkData>> LoadAsync(LogicContext context, ExternalFeedData feed,
            Func<ExternalFeedData, CancellationToken, Task<IReadOnlyList<ExternalWorkData>>> fetch, CancellationToken ct, bool refresh = false)
        {
            var local = await context.ExecuteBatchAsync((ctx, token) => ReadAsync(ctx, feed, token), ct, mode: SqlAccessMode.ReadOnly);
            if (!refresh && local.Items is not null)
            {
                return local.Items;
            }

            var gate = local.Feed.Namespace switch
            {
                "tmdb_movie" or "tmdb_tv" => _tmdb,
                "anilist" => _aniList,
                "openlibrary" => _books,
                _ => throw new InvalidOperationException("This provider has no Discover feeds.")
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var entered = false;
            var attempted = false;
            var failedKey = "external.feed.failed." + (local.Feed.Namespace is "tmdb_movie" or "tmdb_tv" ? "tmdb" : local.Feed.Namespace);
            try
            {
                await gate.WaitAsync(timeout.Token);
                entered = true;
                if (!refresh || local.Items is null)
                {
                    var filled = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadExternalFeedAsync(local.Feed, token), timeout.Token, mode: SqlAccessMode.ReadOnly);
                    if (filled is not null)
                    {
                        return filled;
                    }
                }

                IReadOnlyList<ExternalWorkData> works;
                try
                {
                    if (cache.TryGetValue(failedKey, out _))
                    {
                        throw new ServiceUnavailableException("metadata_unavailable");
                    }
                    attempted = true;
                    works = await fetch(local.Feed, timeout.Token);
                }
                catch (ServiceUnavailableException)
                {
                    if (attempted)
                    {
                        cache.Set(failedKey, true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1), Size = 1 });
                    }
                    throw;
                }

                if (refresh && works.Count == 0 && local.Items is { Count: > 0 })
                {
                    throw new ServiceUnavailableException("metadata_unavailable");
                }
                await context.ExecuteBatchAsync(async (ctx, token) =>
                {
                    await ctx.SaveExternalFeedAsync(local.Feed, works, token);
                    return true;
                }, timeout.Token);
                return await context.ExecuteBatchAsync((ctx, token) => ctx.ReadExternalFeedAsync(local.Feed, token), timeout.Token, mode: SqlAccessMode.ReadOnly)
                    ?? throw new InvalidOperationException("The saved feed is unavailable.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempted)
                {
                    cache.Set(failedKey, true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1), Size = 1 });
                }
                throw new ServiceUnavailableException("metadata_unavailable");
            }
            finally
            {
                if (entered)
                {
                    gate.Release();
                }
            }
        }

        public void Dispose()
        {
            _tmdb.Dispose();
            _aniList.Dispose();
            _books.Dispose();
        }
    }
}
