using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Integrations.Logic.Tmdb;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DiscoverTests
    {
        private const string Token = "eyJ-discover-token";

        [TestMethod]
        public async Task Discover_FeedsSearchAndRequest_ResolveOneCanonicalWork()
        {
            await using var host = await CreateHostAsync();

            // Feeds come from TMDB with the viewer state of each title.
            using var feed = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Trending));
            Assert.AreEqual(HttpStatusCode.OK, feed.StatusCode, await feed.Content.ReadAsStringAsync());
            var trending = (await feed.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Single();
            Assert.AreEqual(603, trending.TmdbId);
            Assert.AreEqual(MediaStateType.NotRequested, trending.MediaStateTypeId);
            Assert.AreEqual("https://image.tmdb.org/t/p/w500/poster.jpg", trending.PosterUrl);
            using var upcomingSeries = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Series, DiscoverFeedType.Upcoming));
            Assert.AreEqual(HttpStatusCode.BadRequest, upcomingSeries.StatusCode);

            // Requesting a provider title creates the Work once and the request.
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/Movie/603/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/Movie/603/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionRequests\""));
            Assert.AreEqual("A hacker learns the truth.", await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Overview'"));

            using var item = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/Movie/603");
            var details = (await item.Content.ReadFromJsonAsync<DiscoverItemV1>())!;
            Assert.AreEqual(MediaStateType.Requested, details.MediaStateTypeId);
            Assert.IsNotNull(details.WorkId);

            // Search finds the local Work first and does not repeat it from TMDB.
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/search", new SearchDiscoverDataV1("Matrix"));
            var results = (await searched.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.ToArray();
            Assert.AreEqual(details.WorkId, results[0].WorkId);
            Assert.AreEqual(1, results.Count(result => result.TmdbId == 603));
            Assert.IsTrue(results.Any(result => result.MediaTypeId == MediaType.Series && result.Title == "The Matrix Show"));

            // Approval makes it part of the library.
            await host.Fixture.RunSqlAsync("INSERT INTO \"WantedItems\" (\"WorkId\") SELECT \"Id\" FROM \"Works\"");
            using var library = await host.SendAsync(HttpMethod.Get, "/api/v1/library?query=matr");
            Assert.AreEqual(HttpStatusCode.OK, library.StatusCode, await library.Content.ReadAsStringAsync());
            var entry = (await library.Content.ReadFromJsonAsync<PageResult<LibraryItemV1>>())!.Items.Single();
            Assert.AreEqual(MediaStateType.Requested, entry.MediaStateTypeId);
            Assert.AreEqual("https://image.tmdb.org/t/p/w500/poster.jpg", entry.PosterUrl);
        }

        [TestMethod]
        public async Task Razor_HomeLibraryAndProviderTitle_RenderRealData()
        {
            await using var host = await CreateHostAsync(razor: true);

            using var home = await host.SendAsync(HttpMethod.Get, "/");
            var html = await home.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, home.StatusCode, html);
            StringAssert.Contains(html, "The Matrix");
            StringAssert.Contains(html, "media-hero");
            StringAssert.Contains(html, "/Discover/movies/603");

            using var browse = await host.SendAsync(HttpMethod.Get, "/?type=series&view=Top");
            StringAssert.Contains(await browse.Content.ReadAsStringAsync(), "The Matrix Show");

            using var title = await host.SendAsync(HttpMethod.Get, "/Discover/movies/603");
            html = await title.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, title.StatusCode, html);
            StringAssert.Contains(html, "A hacker learns the truth.");
            using var requested = await host.SendAsync(HttpMethod.Post, "/Discover/movies/603", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = LibraryTests.Token(html), ["UiLocaleId"] = "1"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, requested.StatusCode, await requested.Content.ReadAsStringAsync());

            using var library = await host.SendAsync(HttpMethod.Get, "/Library?type=movies");
            Assert.AreEqual(HttpStatusCode.OK, library.StatusCode);
            using var search = await host.SendAsync(HttpMethod.Get, "/?q=Matrix");
            StringAssert.Contains(await search.Content.ReadAsStringAsync(), "The Matrix Show");
        }

        private static async Task<Host> CreateHostAsync(bool razor = false)
        {
            var tmdb = new FakeTmdb();
            var host = await Host.CreateAsync(razor: razor, library: true, services: services => services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => tmdb));
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            var provider = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'tmdb'"))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret", new SaveProviderSecretDataV1(Token));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            return host;
        }

        private sealed class FakeTmdb : HttpMessageHandler
        {
            private const string Movie = """{"id":603,"title":"The Matrix","original_title":"The Matrix","release_date":"1999-03-31","overview":"A hacker learns the truth.","poster_path":"/poster.jpg","backdrop_path":"/backdrop.jpg"}""";
            private const string Show = """{"id":1399,"name":"The Matrix Show","original_name":"The Matrix Show","first_air_date":"2020-01-01","overview":"A show.","poster_path":"/show.jpg"}""";

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Headers.Authorization?.Parameter != Token) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                var body = request.RequestUri!.AbsolutePath switch
                {
                    "/3/trending/movie/week" or "/3/movie/top_rated" or "/3/movie/now_playing" or "/3/movie/upcoming" or "/3/search/movie" => "{\"results\":[" + Movie + "]}",
                    "/3/trending/tv/week" or "/3/tv/top_rated" or "/3/tv/on_the_air" or "/3/search/tv" => "{\"results\":[" + Show + "]}",
                    "/3/movie/603" => Movie[..^1] + ",\"external_ids\":{\"imdb_id\":\"tt0133093\"}}",
                    _ => null
                };
                return Task.FromResult(body is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }
    }
}
