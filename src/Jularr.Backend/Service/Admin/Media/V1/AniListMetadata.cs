using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Logic.AniList;
using Microsoft.Extensions.Caching.Memory;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // AniList lookups, shared and cached; Manga and Light Novels import as Book Works with their known volumes.
    internal sealed class AniListMetadata(AniListClient aniList, IMemoryCache cache)
    {
        public const string Namespace = "anilist";
        private static readonly TimeSpan SearchLifetime = TimeSpan.FromHours(1);
        private static readonly TimeSpan ReadLifetime = TimeSpan.FromHours(12);

        public async Task<IReadOnlyList<AniListMedia>> SearchAsync(AniListMediaType type, IReadOnlyCollection<string> formats, string query, CancellationToken ct)
        {
            var key = $"anilist.search.{type}.{string.Join(',', formats.Order(StringComparer.Ordinal))}.{query.Trim().ToLowerInvariant()}";
            if (cache.TryGetValue(key, out IReadOnlyList<AniListMedia>? cached))
            {
                return cached!;
            }

            try
            {
                var results = await aniList.SearchAsync(type, formats, query, ct);
                cache.Set(key, results, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = SearchLifetime, Size = Math.Max(1, results.Count) });
                return results;
            }
            catch (AniListException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<AniListMedia> ReadAsync(AniListMediaType type, int id, CancellationToken ct)
        {
            var key = $"anilist.read.{type}.{id.ToString(CultureInfo.InvariantCulture)}";
            if (cache.TryGetValue(key, out AniListMedia? cached))
            {
                return cached!;
            }

            try
            {
                var media = await aniList.ReadAsync(type, id, ct);
                cache.Set(key, media, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ReadLifetime, Size = 1 });
                return media;
            }
            catch (AniListException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<IReadOnlyList<AniListMedia>> SearchBooksAsync(MediaSubtype subtype, string query, int? year, CancellationToken ct)
        {
            var results = await SearchAsync(AniListMediaType.Manga, AniListMatching.Formats(MediaType.Book, subtype), query, ct);
            return year is { } wanted ? [.. results.OrderByDescending(result => result.Year == wanted)] : results;
        }

        public async Task<long> ImportAsync(LogicContext context, string id, CancellationToken ct)
        {
            // Read before the short write batch; the format names the subtype.
            var media = await ReadAsync(AniListMediaType.Manga, int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var mediaId) ? mediaId : throw new KeyNotFoundException("Invalid AniList id."), ct);

            // Releases use the English or the romanized name; both stay titles.
            var title = Title(media);
            var facts = new[]
            {
                new { Key = "Author", Value = TextLimit.Cut(string.Join(", ", media.Authors), 4000), Localized = false },
                new { Key = "Overview", Value = TextLimit.Cut(media.Description, 4000), Localized = false },
                new { Key = "Poster", Value = media.CoverUrl, Localized = false }
            }.Where(fact => !string.IsNullOrWhiteSpace(fact.Value));
            var volumes = Enumerable.Range(1, media.Volumes ?? 0).Select(volume => new { Volume = volume });
            var data = new WorkMetadataData(MediaType.Book, AniListMatching.Subtype(media.Format) ?? MediaSubtype.Manga, Namespace, media.Id.ToString(CultureInfo.InvariantCulture),
                TextLimit.Cut(title, 500)!, TextLimit.Cut(media.EnglishTitle is null ? media.NativeTitle : media.RomajiTitle, 500), media.Year is >= 1800 and <= 2200 ? media.Year : null, "[]",
                JsonSerializer.Serialize(volumes), JsonSerializer.Serialize(facts), Namespace, null);
            return await context.ExecuteBatchAsync((ctx, token) => ctx.SaveWorkMetadataAsync(data, token), ct);
        }

        public static string Title(AniListMedia media) => media.EnglishTitle ?? media.RomajiTitle;

        private static Exception Translate(AniListException exception) => exception.Failure switch
        {
            AniListFailure.NotFound => new KeyNotFoundException("AniList does not know this title."),
            AniListFailure.RateLimited => new ServiceUnavailableException("metadata_rate_limited"),
            _ => new ServiceUnavailableException("metadata_unavailable")
        };
    }
}
