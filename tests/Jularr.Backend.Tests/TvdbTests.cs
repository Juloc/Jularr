using System.Net;
using System.Text;
using Jularr.Integrations.Data.Tvdb;
using Jularr.Integrations.Logic.Tvdb;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class TvdbTests
    {
        [TestMethod]
        public async Task Login_UsesProjectKey_WithoutSendingPinWhenAbsent()
        {
            using var client = CreateClient((request, _) =>
            {
                Assert.AreEqual(HttpMethod.Post, request.Method);
                Assert.AreEqual("/v4/login", request.RequestUri!.AbsolutePath);
                var payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                StringAssert.Contains(payload, "\"apikey\":\"project-key\"");
                Assert.IsFalse(payload.Contains("pin", StringComparison.Ordinal));
                return Reply("""{"data":{"token":"bearer-secret"}}""");
            });

            var session = await new TvdbClient(client).LoginAsync("project-key", null, CancellationToken.None);
            Assert.AreEqual("bearer-secret", session.Token);
            Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(session).Contains("bearer-secret", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task ImdbLookup_RejectsMultipleSeries_AndIgnoresMovies()
        {
            using var client = CreateClient((request, _) =>
            {
                Assert.AreEqual("/v4/search/remoteid/tt1234567", request.RequestUri!.AbsolutePath);
                Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
                Assert.AreEqual("session", request.Headers.Authorization.Parameter);
                return Reply("""{"data":[{"movie":{"id":2}},{"series":{"id":71}},{"series":{"id":72}}]}""");
            });
            var service = new TvdbClient(client);
            var error = await Assert.ThrowsExactlyAsync<TvdbException>(() => service.FindSeriesByImdbAsync(new TvdbSession("session"), "tt1234567", CancellationToken.None));
            Assert.AreEqual(TvdbFailure.Ambiguous, error.Failure);
        }

        [TestMethod]
        public async Task Search_SeriesOnly_WithYearPageAndNamesPerLanguage()
        {
            using var client = CreateClient((request, _) =>
            {
                Assert.AreEqual("/v4/search", request.RequestUri!.AbsolutePath);
                StringAssert.Contains(request.RequestUri.Query, "query=The%20Show&type=series&year=2020&offset=20&limit=20");
                return Reply("""
                    {"data":[{"tvdb_id":"81189","name":"Die Serie","year":"2020","type":"series","primary_language":"deu","image_url":"/banners/81189.jpg","overview":"Eine Serie.",
                    "translations":{"deu":"Die Serie","eng":"The Show"},"overviews":{"eng":"A show."}},
                    {"tvdb_id":"81189","name":"Die Serie","type":"series"},{"tvdb_id":"5","name":"A film","type":"movie"},{"name":"No id","type":"series"}]}
                    """);
            });
            var result = (await new TvdbClient(client).SearchSeriesAsync(new TvdbSession("session"), "The Show", 2020, 20, 20, CancellationToken.None)).Single();
            Assert.AreEqual((81189, "Die Serie", (int?)2020, "deu", "https://artworks.thetvdb.com/banners/81189.jpg"), (result.Id, result.Name, result.Year, result.OriginalLanguage, result.ImageUrl));
            Assert.AreEqual(("The Show", "A show."), (result.Names["eng"], result.Overviews["eng"]));
        }

        [TestMethod]
        public async Task Episodes_IncludeSpecialsAndAllPages_WithoutRepeats()
        {
            using var client = CreateClient((request, _) =>
            {
                Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
                Assert.AreEqual("session", request.Headers.Authorization.Parameter);
                return request.RequestUri!.Query.Contains("page=0", StringComparison.Ordinal)
                    ? Reply("""{"data":{"episodes":[{"id":11,"seasonNumber":0,"number":1,"absoluteNumber":0,"name":"OVA","aired":"2025-01-01"}]},"links":{"next":"?page=1"}}""")
                    : Reply("""{"data":{"episodes":[{"id":12,"seasonNumber":1,"number":1,"absoluteNumber":1,"name":"Pilot","runtime":24}]},"links":{"next":null}}""");
            });
            var result = await new TvdbClient(client).ReadEpisodesAsync(new TvdbSession("session"), 123, null, CancellationToken.None);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual(0, result[0].SeasonNumber);
            Assert.AreEqual(1, result[1].SeasonNumber);
            Assert.AreEqual(1, result[1].AbsoluteNumber);
        }

        [TestMethod]
        public async Task Episodes_InALanguage_ReadItsTranslationsWithSummaryAndImage()
        {
            using var client = CreateClient((request, _) =>
            {
                Assert.AreEqual("/v4/series/123/episodes/default/deu", request.RequestUri!.AbsolutePath);
                return Reply("""{"data":{"episodes":[{"id":12,"seasonNumber":1,"number":1,"name":"Pilotfolge","overview":"Es beginnt.","image":"/banners/episodes/123/12.jpg"}]},"links":{"next":null}}""");
            });
            var episode = (await new TvdbClient(client).ReadEpisodesAsync(new TvdbSession("session"), 123, "deu", CancellationToken.None)).Single();
            Assert.AreEqual("Pilotfolge", episode.Name);
            Assert.AreEqual("Es beginnt.", episode.Overview);
            Assert.AreEqual("https://artworks.thetvdb.com/banners/episodes/123/12.jpg", episode.ImageUrl);
        }

        [TestMethod]
        public async Task Series_ReadsEverythingAnImportNeeds()
        {
            using var client = CreateClient((request, _) =>
            {
                Assert.AreEqual("/v4/series/81189/extended", request.RequestUri!.AbsolutePath);
                StringAssert.Contains(request.RequestUri.Query, "meta=translations");
                StringAssert.Contains(request.RequestUri.Query, "short=false");
                return Reply(FullSeries);
            });

            var series = await new TvdbClient(client).ReadSeriesAsync(new TvdbSession("session"), 81189, CancellationToken.None);
            Assert.AreEqual("Breaking Bad", series.Name);
            Assert.AreEqual("tt0903747", series.ImdbId);
            Assert.AreEqual(1396, series.TmdbId);
            Assert.AreEqual("eng", series.OriginalLanguage);
            Assert.AreEqual("usa", series.OriginalCountry);
            Assert.AreEqual(new DateOnly(2008, 1, 20), series.FirstAired);
            Assert.AreEqual("Ended", series.Status);
            Assert.AreEqual("Breaking Bad (DE)", series.Translations.Single(translation => translation.Language == "deu").Name);
            Assert.AreEqual("Ein Lehrer kocht.", series.Translations.Single(translation => translation.Language == "deu").Overview);
            Assert.IsFalse(series.Translations.Any(translation => translation.Name == "BB"));
            Assert.AreEqual("BB", series.Aliases.Single().Name);
            Assert.AreEqual(3, series.Artworks.Count);
            Assert.AreEqual(TvdbArtworkType.ClearLogo, series.Artworks[2].Type);
            CollectionAssert.AreEqual(new[] { "Drama", "Crime" }, series.Genres.ToArray());
            CollectionAssert.AreEqual(new[] { "AMC", "Sony Pictures Television" }, series.Networks.ToArray());
            Assert.AreEqual("HhesaQXLuRY", series.Trailers.Single().YouTubeKey);
            var actor = series.Characters.Single(character => character.Role == "Actor");
            Assert.AreEqual(("Bryan Cranston", "Walter White", 300, "https://artworks.thetvdb.com/person/300.jpg"), (actor.PersonName, actor.Character, actor.PersonId, actor.ImageUrl));
            Assert.AreEqual("https://artworks.thetvdb.com/banners/seasons/1.jpg", series.Seasons.Single(season => season.Number == 1).ImageUrl);
            Assert.AreEqual(1, series.Seasons.Count);
        }

        internal const string FullSeries = """
            {"data":{"id":81189,"name":"Breaking Bad","image":"https://artworks.thetvdb.com/banners/posters/81189-1.jpg","overview":"A teacher cooks.","firstAired":"2008-01-20","year":"2008",
            "originalLanguage":"eng","originalCountry":"usa","status":{"id":2,"name":"Ended"},
            "remoteIds":[{"id":"tt0903747","type":2,"sourceName":"IMDB"},{"id":"1396","type":12,"sourceName":"TheMovieDB.com"}],
            "aliases":[{"language":"eng","name":"BB"}],
            "artworks":[{"image":"https://artworks.thetvdb.com/banners/posters/de.jpg","language":"deu","type":2,"score":10},
                {"image":"https://artworks.thetvdb.com/banners/fanart/1.jpg","language":null,"type":3,"score":5},
                {"image":"https://artworks.thetvdb.com/banners/logos/1.png","language":"eng","type":23,"score":1},
                {"image":"https://artworks.thetvdb.com/banners/people/1.jpg","type":13,"score":1}],
            "genres":[{"id":1,"name":"Drama"},{"id":2,"name":"Crime"}],
            "originalNetwork":{"id":1,"name":"AMC"},"latestNetwork":{"id":1,"name":"AMC"},
            "companies":[{"name":"AMC","companyType":{"companyTypeName":"Network"}},{"name":"Sony Pictures Television","companyType":{"companyTypeName":"Studio"}},
                {"name":"Gran Via","companyType":{"companyTypeName":"Production Company"}}],
            "trailers":[{"url":"https://www.youtube.com/watch?v=HhesaQXLuRY","language":"eng"},{"url":"https://vimeo.com/1","language":"eng"}],
            "characters":[{"name":"Walter White","peopleId":300,"peopleType":"Actor","personName":"Bryan Cranston","personImgURL":"https://artworks.thetvdb.com/person/300.jpg","sort":1},
                {"name":"Vince Gilligan","peopleId":301,"peopleType":"Creator","personName":"Vince Gilligan","sort":2}],
            "seasons":[{"number":1,"type":{"type":"official"},"image":"https://artworks.thetvdb.com/banners/seasons/1.jpg"},{"number":1,"type":{"type":"dvd"},"image":"/dvd.jpg"}],
            "translations":{"nameTranslations":[{"name":"Breaking Bad (DE)","language":"deu"},{"name":"BB","language":"eng","isAlias":true},{"name":"Breaking Bad","language":"eng"}],
                "overviewTranslations":[{"overview":"Ein Lehrer kocht.","language":"deu"},{"overview":"A teacher cooks.","language":"eng"}]}}}
            """;

        [TestMethod]
        public async Task Unauthorized_FailsWithoutRevealingCredential()
        {
            using var client = CreateClient((_, _) => Reply("{}", HttpStatusCode.Unauthorized));
            var error = await Assert.ThrowsExactlyAsync<TvdbException>(() =>
                new TvdbClient(client).ReadSeriesAsync(new TvdbSession("private-token"), 10, CancellationToken.None));
            Assert.AreEqual(TvdbFailure.AuthenticationFailed, error.Failure);
            Assert.IsFalse(error.Message.Contains("private-token", StringComparison.Ordinal));
        }

        private static HttpClient CreateClient(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) =>
            new(new Handler(response)) { BaseAddress = new Uri("https://api4.thetvdb.com/v4/") };

        private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(response(request, cancellationToken));
        }
    }
}
