using System.Text.Json;
using Jularr.Backend.Service.Core;

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
                    }
                }
                """);
            var links = AniBridgeMappings.Parse(json.RootElement);
            Assert.AreEqual(500, links[100].TvdbSeriesId);
            Assert.AreEqual(3, links[100].TvdbSeasonNumber);
            Assert.AreEqual(700, links[100].TmdbSeriesId);
            Assert.AreEqual(800, links[200].TmdbMovieId);
            Assert.IsNull(links[300].TvdbSeriesId);
            Assert.IsNull(links[300].TvdbSeasonNumber);
            Assert.IsFalse(links.ContainsKey(400));
        }

        [TestMethod]
        public void Parse_RejectsBreakingSchema()
        {
            using var json = JsonDocument.Parse("""{"$meta":{"schema_version":"4.0.0"}}""");
            Assert.ThrowsExactly<JsonException>(() => AniBridgeMappings.Parse(json.RootElement));
        }
    }
}