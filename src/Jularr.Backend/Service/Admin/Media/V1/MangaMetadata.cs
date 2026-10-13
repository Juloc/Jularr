using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Logic.AniList;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // AniList lookups and imports for Manga; known volumes become the Work's units.
    internal sealed class MangaMetadata(AniListClient aniList)
    {
        public const string Namespace = "anilist";

        public static bool Handles(MediaType mediaType, MediaSubtype? subtype) => mediaType == MediaType.Book && subtype == MediaSubtype.Manga;

        public async Task<IReadOnlyList<AniListManga>> SearchAsync(string query, int? year, CancellationToken ct)
        {
            try
            {
                var results = await aniList.SearchAsync(query, ct);
                return year is { } wanted ? [.. results.OrderByDescending(result => result.Year == wanted)] : results;
            }
            catch (AniListException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<long> ImportAsync(LogicContext context, string id, CancellationToken ct)
        {
            // Read before the short write batch.
            AniListManga manga;
            try
            {
                manga = await aniList.ReadAsync(int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var mediaId) ? mediaId : throw new KeyNotFoundException("Invalid AniList id."), ct);
            }
            catch (AniListException exception)
            {
                throw Translate(exception);
            }

            // Releases use the English or the romanized name; both stay titles.
            var title = Title(manga);
            var facts = new[]
            {
                new { Key = "Author", Value = Limit(string.Join(", ", manga.Authors), 4000), Localized = false },
                new { Key = "Overview", Value = Limit(manga.Description, 4000), Localized = false },
                new { Key = "Poster", Value = manga.CoverUrl, Localized = false }
            }.Where(fact => !string.IsNullOrWhiteSpace(fact.Value));
            var volumes = Enumerable.Range(1, manga.Volumes ?? 0).Select(volume => new { Volume = volume });
            var data = new WorkMetadataData(MediaType.Book, MediaSubtype.Manga, Namespace, manga.Id.ToString(CultureInfo.InvariantCulture), Limit(title, 500)!,
                Limit(manga.EnglishTitle is null ? manga.NativeTitle : manga.RomajiTitle, 500), manga.Year is >= 1800 and <= 2200 ? manga.Year : null, "[]", JsonSerializer.Serialize(volumes),
                JsonSerializer.Serialize(facts), Namespace);
            return await context.ExecuteBatchAsync((ctx, token) => ctx.SaveWorkMetadataAsync(data, token), ct);
        }

        public static string Title(AniListManga manga) => manga.EnglishTitle ?? manga.RomajiTitle;

        private static Exception Translate(AniListException exception) => exception.Failure switch
        {
            AniListFailure.NotFound => new KeyNotFoundException("AniList does not know this Manga."),
            AniListFailure.RateLimited => new ServiceUnavailableException("metadata_rate_limited"),
            _ => new ServiceUnavailableException("metadata_unavailable")
        };

        private static string? Limit(string? value, int length) => value is null ? null : value.Length <= length ? value : value[..length];
    }
}
