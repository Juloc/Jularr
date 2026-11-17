using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Integrations.Data.Newznab;

namespace Jularr.Backend.Tests
{
    // The first round of a search is what Sonarr asks first; everything else only runs when it found nothing.
    [TestClass]
    public sealed class SearchPlannerTests
    {
        private static readonly NewznabCapabilities Caps = new(DateTimeOffset.MinValue, new Dictionary<NewznabSearchMode, string[]>
        {
            [NewznabSearchMode.Search] = ["q"],
            [NewznabSearchMode.TvSearch] = ["q", "season", "ep", "tvdbid", "imdbid"],
            [NewznabSearchMode.Movie] = ["q", "imdbid", "tmdbid"]
        });

        private static string[] FirstRound(SearchIntent intent) =>
            [.. SearchPlanner.Plan(intent, Caps, SearchDepth.Normal).Where(query => query.Tier == 0)
                .Select(query => $"{query.Mode}|{query.Text}|{string.Join('&', query.Parameters.Select(pair => $"{pair.Key}={pair.Value}"))}")];

        private static readonly Dictionary<string, string> Ids = new() { ["tvdb"] = "42", ["imdb"] = "tt7" };

        [TestMethod]
        public void Episode_AsksOnlyTheSeriesIdWithSeasonAndEpisodeFirst()
        {
            var intent = new SearchIntent(AcquisitionKind.Tv, "Show") { ExternalIds = Ids, Season = 1, Episode = 5, Aliases = ["Alias"] };
            CollectionAssert.AreEqual(new[] { "TvSearch||tvdbid=42&season=1&ep=5" }, FirstRound(intent));
            Assert.IsTrue(SearchPlanner.Plan(intent, Caps, SearchDepth.Normal).Count(query => query.Tier > 0) > 0, "The titles stay as the fallback.");
        }

        [TestMethod]
        public void Episode_WithoutAnId_AsksTheTitleFirst()
        {
            var first = FirstRound(new SearchIntent(AcquisitionKind.Tv, "Show") { Season = 1, Episode = 5 });
            CollectionAssert.Contains(first, "TvSearch|Show|season=1&ep=5");
            Assert.IsFalse(first.Any(query => query.Contains("tvdbid", StringComparison.Ordinal)));
        }

        [TestMethod]
        public void Anime_AsksOnlyTheIdWithSeasonAndEpisodeFirst_TheAbsoluteNumberOnlyAfter()
        {
            var intent = new SearchIntent(AcquisitionKind.Anime, "Show") { ExternalIds = Ids, Season = 1, Episode = 5, AbsoluteEpisode = 17 };
            CollectionAssert.AreEqual(new[] { "TvSearch||tvdbid=42&season=1&ep=5" }, FirstRound(intent));
            var fallback = SearchPlanner.Plan(intent, Caps, SearchDepth.Normal).Where(query => query.Tier > 0).ToArray();
            Assert.IsTrue(fallback.Any(query => query.Text == "17") && fallback.Any(query => query.Text == "Show 17"));
        }

        [TestMethod]
        public void Special_IsAskedByItsOwnTitle_ADailyShowByDate()
        {
            CollectionAssert.Contains(FirstRound(new SearchIntent(AcquisitionKind.Tv, "Show") { ExternalIds = Ids, Season = 0, Episode = 2, EpisodeTitle = "The Making Of" }),
                "Search|Show The Making Of|");
            CollectionAssert.AreEqual(new[] { "TvSearch||tvdbid=42&season=2024&ep=03/09" },
                FirstRound(new SearchIntent(AcquisitionKind.Tv, "Show") { ExternalIds = Ids, Season = 2024, Episode = 30, AirDate = new DateOnly(2024, 3, 9) }));
        }

        [TestMethod]
        public void Movie_AsksTheIdFirst_TheTitleOnlyWithoutOne()
        {
            CollectionAssert.AreEqual(new[] { "Movie||imdbid=7" }, FirstRound(new SearchIntent(AcquisitionKind.Movie, "Film") { ExternalIds = Ids, Year = 2020 }));
            CollectionAssert.AreEqual(new[] { "Search|Film 2020|" }, FirstRound(new SearchIntent(AcquisitionKind.Movie, "Film") { Year = 2020 }));
        }
    }
}
