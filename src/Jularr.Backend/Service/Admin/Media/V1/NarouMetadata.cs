using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Narou;
using Jularr.Integrations.Logic.Narou;
using Microsoft.Extensions.Caching.Memory;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // Narō's web novels: found by title or ncode through its API and kept as Light Novel Works under their ncode.
    // A web novel is its own Work; it is never taken for a published edition because a title is the same.
    internal sealed class NarouMetadata(NarouClient narou, IMemoryCache cache)
    {
        public const string Namespace = WorkMetadataLogic.WebNovelNamespace;
        private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

        // The provider switch of Admin > Providers decides whether Discover and the Works page ask Narō.
        public static async Task<bool> IsEnabledAsync(LogicContext context, CancellationToken ct) =>
            await context.ExecuteBatchAsync((ctx, token) => ctx.ReadProviderCredentialAsync(new ProviderParameters(Namespace), token), ct, mode: SqlAccessMode.ReadOnly) is { IsEnabled: true };

        public async Task<IReadOnlyList<NarouNovel>> SearchAsync(string query, CancellationToken ct)
        {
            var key = "narou.search." + query.Trim().ToLowerInvariant();
            if (cache.TryGetValue(key, out IReadOnlyList<NarouNovel>? cached)) return cached!;
            try
            {
                var found = await narou.SearchAsync(query, ct);
                cache.Set(key, found, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime, Size = Math.Max(1, found.Count) });
                return found;
            }
            catch (NarouException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<NarouNovel> ReadAsync(string ncode, CancellationToken ct)
        {
            var key = "narou.read." + ncode.ToLowerInvariant();
            if (cache.TryGetValue(key, out NarouNovel? cached)) return cached!;
            try
            {
                var novel = await narou.ReadAsync(ncode, ct) ?? throw new KeyNotFoundException("Narō does not know this ncode.");
                cache.Set(key, novel, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime, Size = 1 });
                return novel;
            }
            catch (NarouException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<long> ImportAsync(LogicContext context, string ncode, CancellationToken ct)
        {
            // Read before the short write batch; Narō's novels are written in Japanese.
            var novel = await ReadAsync(ncode, ct);
            var facts = new[] { new { Key = "Overview", Value = TextLimit.Cut(novel.Story, 4000), Localized = false } }.Where(fact => !string.IsNullOrWhiteSpace(fact.Value));
            var data = new WorkMetadataData(MediaType.Book, MediaSubtype.LightNovel, Namespace, novel.Ncode, TextLimit.Cut(novel.Title, 500)!, null, novel.FirstYear is >= 1800 and <= 2200 ? novel.FirstYear : null,
                "[]", "[]", JsonSerializer.Serialize(facts), Namespace, null) { OriginalLanguage = "ja" };
            var people = WorkPeopleLogic.Rows(novel.Writer.Length == 0 ? [] : [new ProviderPerson(null, novel.Writer, ProviderPerson.Author)]).ToArray();
            return await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                var workId = await ctx.SaveWorkMetadataAsync(data, token);
                await ctx.SaveWorkPeopleAsync(WorkPeopleLogic.Data(workId, Namespace, people), token);
                return workId;
            }, ct);
        }

        private static Exception Translate(NarouException exception) => exception.Failure == NarouFailure.Missing
            ? new KeyNotFoundException("Narō does not know this novel.")
            : new ServiceUnavailableException("metadata_unavailable");
    }
}
