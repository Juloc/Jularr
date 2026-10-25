using System.Text.Json;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Data.Logic.Media;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AniBridgeTests
    {
        [TestMethod]
        public void Parse_MapsUnambiguousSeasonsAndMovies_AndRefusesAmbiguousSeasons()
        {
            using var json = JsonDocument.Parse("""
                {
                    "$meta": { "schema_version": "3.0.3", "generated_on": "2026-10-23T00:00:00Z" },
                    "anilist:100": {
                        "tvdb_show:500:s3": { "1-12": "1-12" },
                        "tmdb_show:700:s3": { "1-12": "1-12" }
                    },
                    "anilist:200": {
                        "tmdb_movie:800": { "1": "1" }
                    },
                    "anilist:300": {
                        "tvdb_show:500:s2": { "1-6": "1-6" },
                        "tvdb_show:500:s3": { "1-6": "1-6" }
                    },
                    "anilist:400": {
                        "tvdb_show:900:s1": { "1-12": null }
                    },
                    "anilist:500": {
                        "tvdb_show:901:s1": {}
                    },
                    "anilist:600": {
                        "tvdb_show:902:s2": { "1-6": "1-6" },
                        "tvdb_show:902:s3": { "7-12": "1-6" }
                    }
                }
                """);
            var links = AniBridgeMappings.Parse(json.RootElement);
            Assert.AreEqual(500, links[100].TvdbSeriesId);
            Assert.AreEqual(3, links[100].TvdbSeasonNumber);
            Assert.AreEqual(700, links[100].TmdbSeriesId);
            Assert.AreEqual(800, links[200].TmdbMovieId);
            Assert.AreEqual(500, links[300].TvdbSeriesId);
            Assert.AreEqual(2, links[300].TvdbSeasonNumber);
            Assert.IsFalse(links.ContainsKey(400));
            Assert.AreEqual(901, links[500].TvdbSeriesId);
            Assert.AreEqual(902, links[600].TvdbSeriesId);
            Assert.AreEqual(2, links[600].TvdbSeasonNumber);
        }

        [TestMethod]
        public void AlignTvdb_MapsTwoCoursAndOpenEndedRanges()
        {
            using var json = JsonDocument.Parse("""
                {
                    "tvdb_show:500:s2": {
                        "anilist:20": { "1-12": "1-12" },
                        "anilist:30": { "13-24": "1-12" }
                    },
                    "tvdb_show:600:s1": {
                        "anilist:40": { "1-": "1-" }
                    }
                }
                """);
            var result = AniBridgeMappings.AlignTvdb(json.RootElement, 500, [new SeasonEpisodesData(2, 24)]);
            Assert.IsNotNull(result);
            Assert.IsFalse(result.RequiresReview);
            CollectionAssert.AreEqual(new[] { (2, 20, 0), (2, 30, 12) }, result.Seasons.ToArray());
            var openEnded = AniBridgeMappings.AlignTvdb(json.RootElement, 600, [new SeasonEpisodesData(1, 12)]);
            Assert.IsNotNull(openEnded);
            CollectionAssert.AreEqual(new[] { (1, 40, 0) }, openEnded.Seasons.ToArray());
        }

        [TestMethod]
        public void AlignTvdb_UnsafeRangesRequireReview()
        {
            using var json = JsonDocument.Parse("""
                {
                    "tvdb_show:500:s1": { "anilist:20": { "1-12": "1-6|2" } },
                    "tvdb_show:600:s1": { "anilist:30": { "1-12": "1-12" } },
                    "tvdb_show:600:s2": { "anilist:30": { "1-12": "1-12" } }
                }
                """);
            var scaled = AniBridgeMappings.AlignTvdb(json.RootElement, 500, [new SeasonEpisodesData(1, 12)]);
            Assert.IsNotNull(scaled);
            Assert.IsTrue(scaled.RequiresReview);
            Assert.AreEqual(0, scaled.Seasons.Count);
            var shared = AniBridgeMappings.AlignTvdb(json.RootElement, 600, [new SeasonEpisodesData(1, 12), new SeasonEpisodesData(2, 12)]);
            Assert.IsNotNull(shared);
            Assert.IsTrue(shared.RequiresReview);
        }

        [TestMethod]
        public void Parse_RejectsBreakingSchema()
        {
            using var json = JsonDocument.Parse("""{"$meta":{"schema_version":"4.0.0"}}""");
            Assert.ThrowsExactly<JsonException>(() => AniBridgeMappings.Parse(json.RootElement));
        }
    }
}