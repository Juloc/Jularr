using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Data.Tmdb;

namespace Jularr.Backend.Logic.Media
{
    // How a canonical Work finds its AniList entries; AniList never creates or merges Works.
    internal static partial class AniListMatching
    {
        private static readonly HashSet<string> SeriesFormats = ["TV", "TV_SHORT", "ONA"];
        private static readonly HashSet<string> MovieFormats = ["MOVIE"];
        private static readonly HashSet<string> MangaFormats = ["MANGA", "ONE_SHOT"];
        private static readonly HashSet<string> NovelFormats = ["NOVEL"];

        [GeneratedRegex(@"[^\p{L}\p{N}]+")]
        private static partial Regex Separators();

        // TMDB animation from Japan is proposed as Anime; a manual choice always wins.
        public static bool IsAnime(IReadOnlyList<int> genreIds, string? originalLanguage, IReadOnlyList<string> originCountries) =>
            genreIds.Contains(16) && (originalLanguage == "ja" || originCountries.Contains("JP", StringComparer.Ordinal));

        public static MediaSubtype? Subtype(string? format) => format switch
        {
            "MANGA" or "ONE_SHOT" => MediaSubtype.Manga,
            "NOVEL" => MediaSubtype.LightNovel,
            _ => null
        };

        public static AniListMediaType? Type(MediaType mediaType, MediaSubtype? subtype) => (mediaType, subtype) switch
        {
            (MediaType.Movie or MediaType.Series, MediaSubtype.Anime) => AniListMediaType.Anime,
            (MediaType.Book, MediaSubtype.Manga or MediaSubtype.LightNovel) => AniListMediaType.Manga,
            _ => null
        };

        public static IReadOnlySet<string> Formats(MediaType mediaType, MediaSubtype? subtype) => (mediaType, subtype) switch
        {
            (MediaType.Series, _) => SeriesFormats,
            (MediaType.Movie, _) => MovieFormats,
            (MediaType.Book, MediaSubtype.LightNovel) => NovelFormats,
            _ => MangaFormats
        };

        // Latin accents fold away; Japanese voicing marks stay.
        public static string Normalize(string title)
        {
            var folded = new StringBuilder(title.Length);
            foreach (var character in title.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark || folded.Length == 0 || folded[^1] > '\u024F')
                {
                    folded.Append(char.ToLowerInvariant(character));
                }
            }

            return Separators().Replace(folded.ToString().Normalize(NormalizationForm.FormC), " ").Trim();
        }

        // Exactly one entry of a fitting format with one of the titles and, where known, a year within one is a match; anything else needs a person.
        public static (AniListMedia? Match, IReadOnlyList<AniListMedia> Candidates) Choose(IReadOnlyList<AniListMedia> results, IEnumerable<string> titles, int? year, IReadOnlySet<string> formats)
        {
            var names = titles.Select(Normalize).Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);
            var fitting = results.Where(result => result.Format is { } format && formats.Contains(format)).ToArray();
            var exact = fitting.Where(result => Names(result).Any(names.Contains) && (year is null || result.Year is null || Math.Abs(result.Year.Value - year.Value) <= 1)).ToArray();
            return exact.Length == 1 ? (exact[0], exact) : (null, exact.Length > 0 ? exact : fitting.Take(5).ToArray());
        }

        // Consecutive entries fill the regular seasons in order; an entry running past its season ends the alignment.
        public static IReadOnlyList<(int Season, int AniListId, int Offset)> Align(IReadOnlyList<(int Season, int Episodes)> seasons, IReadOnlyList<(int Id, int? Episodes)> chain)
        {
            var aligned = new List<(int, int, int)>();
            var entry = 0;
            foreach (var (season, episodes) in seasons.OrderBy(season => season.Season))
            {
                var offset = 0;
                while (entry < chain.Count && offset < episodes)
                {
                    // A running entry takes the rest of its season and ends the chain.
                    if (chain[entry].Episodes is not { } count)
                    {
                        aligned.Add((season, chain[entry].Id, offset));
                        return aligned;
                    }

                    if (offset + count > episodes)
                    {
                        return aligned;
                    }

                    aligned.Add((season, chain[entry].Id, offset));
                    offset += count;
                    entry++;
                }

                if (entry >= chain.Count)
                {
                    break;
                }
            }

            return aligned;
        }

        // The TMDB series of an anime's first season: exactly one result named like it and, where known, started within a year.
        public static int? ChooseSeries(IReadOnlyList<TmdbSearchResult> results, AniListMedia first)
        {
            var names = Names(first).Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);
            var exact = results.Where(result => (names.Contains(Normalize(result.Title)) || result.OriginalTitle is { } original && names.Contains(Normalize(original)))
                && (first.Year is null || result.Year is null || Math.Abs(result.Year.Value - first.Year.Value) <= 1)).ToArray();
            return exact.Length == 1 ? exact[0].Id : null;
        }

        private static IEnumerable<string> Names(AniListMedia media) =>
            new[] { media.RomajiTitle, media.EnglishTitle, media.NativeTitle }.Concat(media.Synonyms).OfType<string>().Select(Normalize);
    }
}
