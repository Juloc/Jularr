using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // Which wanted titles a feed release may be about. This only picks the titles to judge it for; the release itself is judged by the selection of each, as a search's would be.
    // A wrong match costs a judgement, a missed one is found by the title's next search.
    internal static class FeedMatcher
    {
        // Each title with whether the indexer's provider id named it, which counts as a search by id does.
        public static IReadOnlyList<(FeedTargetData Target, bool ViaId)> Match(FeedEntry entry, IReadOnlyList<FeedTargetData> targets)
        {
            var parsed = ReleaseParser.Parse(entry.Title);
            var mediaTypes = ReadMediaTypes(entry, parsed);

            // Provider ids first: an id the indexer names and the target has decides, equal or not.
            var imdb = entry.ImdbId ?? parsed.ImdbId;
            var ids = mediaTypes.Contains(MediaType.Series)
                ? new (string Namespace, string? Value)[] { ("tvdb", entry.TvdbId), ("tmdb_tv", entry.TmdbId), ("imdb", imdb) }
                : [("tmdb_movie", entry.TmdbId), ("imdb", imdb)];
            var matched = new List<(FeedTargetData Target, bool ViaId)>();
            foreach (var target in targets.Where(target => mediaTypes.Contains(target.MediaTypeId)))
            {
                var compared = ids.Where(id => Digits(id.Value).Length > 0)
                    .Select(id => (id.Namespace, Id: Digits(id.Value), Known: Identity(target, id.Namespace))).Where(id => id.Known is not null).ToArray();
                if (compared.Length > 0)
                {
                    if (compared.Any(id => id.Id == id.Known))
                    {
                        matched.Add((target, true));
                    }

                    continue;
                }

                // A book or album names its title among other words and, where the title has one, its author or artist.
                if (target.MediaTypeId is MediaType.Book or MediaType.Music)
                {
                    if (TitleMatcher.MatchesAny(target.Titles.Prepend(target.CanonicalTitle), entry.Title)
                        && (target.Author is not { } author || TitleMatcher.SignificantWords(author).Overlaps(TitleMatcher.SignificantWords(entry.Title))))
                    {
                        matched.Add((target, false));
                    }

                    continue;
                }

                if (TitleMatcher.MatchesAny(target.Titles.Prepend(target.CanonicalTitle), parsed.SeriesTitle))
                {
                    matched.Add((target, false));
                }
            }

            return matched;
        }

        // The category says it; without one the name does: an episode, a season, an air date or an absolute number is a series, anything else a film, a book or an album,
        // which their judges tell apart.
        internal static MediaType[] ReadMediaTypes(FeedEntry entry, ReleaseInfo parsed)
        {
            switch (entry.Category / 1000)
            {
                case 2:
                    return [MediaType.Movie];
                case 3:
                    return [MediaType.Music];
                case 5:
                    return [MediaType.Series];
                case 7:
                    return [MediaType.Book];
            }

            return parsed.SeasonNumber is not null || parsed.EpisodeStart is not null || parsed.AbsoluteEpisodeStart is not null || parsed.AirDate is not null || parsed.IsSeasonPack
                ? [MediaType.Series] : [MediaType.Movie, MediaType.Book, MediaType.Music];
        }

        private static string? Identity(FeedTargetData target, string identityNamespace) =>
            target.ExternalIdentities.Where(identity => identity.StartsWith(identityNamespace + ":", StringComparison.Ordinal))
                .Select(identity => Digits(identity[(identityNamespace.Length + 1)..])).FirstOrDefault(value => value.Length > 0);

        // Ids are compared by their digits: an IMDb id comes with or without "tt".
        private static string Digits(string? value) => new string((value ?? "").Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
    }
}
