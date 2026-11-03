using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // Which wanted targets a feed release is about. This only decides what to search for; the release itself is judged by the search and its selection.
    // A wrong match costs one search, a missed one is found by the weekly search.
    internal static class FeedMatcher
    {
        public static IReadOnlyList<FeedTargetData> Match(FeedEntry entry, IReadOnlyList<FeedTargetData> targets)
        {
            var parsed = ReleaseParser.Parse(entry.Title);
            var mediaType = ReadMediaType(entry, parsed);

            // Provider ids first: an id the indexer names and the target has decides, equal or not.
            var imdb = entry.ImdbId ?? parsed.ImdbId;
            var ids = mediaType == MediaType.Series
                ? new (string Namespace, string? Value)[] { ("tvdb", entry.TvdbId), ("tmdb_tv", entry.TmdbId), ("imdb", imdb) }
                : [("tmdb_movie", entry.TmdbId), ("imdb", imdb)];
            var matched = new List<FeedTargetData>();
            foreach (var target in targets.Where(target => target.MediaTypeId == mediaType))
            {
                var compared = ids.Where(id => Digits(id.Value).Length > 0)
                    .Select(id => (id.Namespace, Id: Digits(id.Value), Known: Identity(target, id.Namespace))).Where(id => id.Known is not null).ToArray();
                if (compared.Length > 0)
                {
                    if (compared.Any(id => id.Id == id.Known))
                    {
                        matched.Add(target);
                    }

                    continue;
                }

                if (TitleMatcher.MatchesAny(target.Titles.Prepend(target.CanonicalTitle), parsed.SeriesTitle))
                {
                    matched.Add(target);
                }
            }

            return matched;
        }

        // The category says it; without one the name does: an episode, a season, an air date or an absolute number is a series.
        internal static MediaType ReadMediaType(FeedEntry entry, ReleaseInfo parsed)
        {
            if (entry.Category / 1000 is 2)
            {
                return MediaType.Movie;
            }

            if (entry.Category / 1000 is 5)
            {
                return MediaType.Series;
            }

            return parsed.SeasonNumber is not null || parsed.EpisodeStart is not null || parsed.AbsoluteEpisodeStart is not null || parsed.AirDate is not null || parsed.IsSeasonPack
                ? MediaType.Series : MediaType.Movie;
        }

        private static string? Identity(FeedTargetData target, string identityNamespace) =>
            target.ExternalIdentities.Where(identity => identity.StartsWith(identityNamespace + ":", StringComparison.Ordinal))
                .Select(identity => Digits(identity[(identityNamespace.Length + 1)..])).FirstOrDefault(value => value.Length > 0);

        // Ids are compared by their digits: an IMDb id comes with or without "tt".
        private static string Digits(string? value) => new string((value ?? "").Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
    }
}
