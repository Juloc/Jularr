using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Provider.V1;
using AdminMedia = Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Integrations.Logic.Tmdb;
using Jularr.Integrations.Data.Tmdb;
using Microsoft.AspNetCore.WebUtilities;
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
            Assert.AreEqual("1999-03-31", await host.ScalarAsync("SELECT \"ReleasedOn\"::text FROM \"Works\""));

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

            // Approval makes it part of the library; the Owner's own request is approved at once.
            await host.Fixture.RunSqlAsync("INSERT INTO \"WantedItems\" (\"WorkId\") SELECT \"Id\" FROM \"Works\" ON CONFLICT (\"WorkId\", \"GamePlatformId\") DO NOTHING");
            using var library = await host.SendAsync(HttpMethod.Get, "/api/v1/library?query=matr");
            Assert.AreEqual(HttpStatusCode.OK, library.StatusCode, await library.Content.ReadAsStringAsync());
            var entry = (await library.Content.ReadFromJsonAsync<PageResult<LibraryItemV1>>())!.Items.Single();
            Assert.AreEqual(MediaStateType.Requested, entry.MediaStateTypeId);
            Assert.AreEqual("https://image.tmdb.org/t/p/w500/poster.jpg", entry.PosterUrl);
        }

        [TestMethod]
        public async Task Franchise_TmdbCollection_ListsTheVisibleTitles()
        {
            await using var host = await CreateHostAsync(razor: true);
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/Movie/603/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;
            var franchiseId = (long)(await host.ScalarAsync("SELECT \"FranchiseId\" FROM \"Works\" WHERE \"Id\" = " + workId))!;
            Assert.AreEqual("The Matrix Collection", await host.ScalarAsync("SELECT \"Name\" FROM \"Franchises\" WHERE \"TmdbCollectionId\" = 2344"));

            // The series lists its titles and the details link to it.
            using var series = await host.SendAsync(HttpMethod.Get, "/api/v1/franchises/" + franchiseId + "/works");
            Assert.AreEqual(HttpStatusCode.OK, series.StatusCode, await series.Content.ReadAsStringAsync());
            Assert.AreEqual(workId, (await series.Content.ReadFromJsonAsync<PageResult<WorkCardV1>>())!.Items.Single().Id);
            using var page = await host.SendAsync(HttpMethod.Get, "/Franchise/" + franchiseId);
            StringAssert.Contains(await page.Content.ReadAsStringAsync(), "The Matrix Collection");
            using var details = await host.SendAsync(HttpMethod.Get, "/Details/" + workId);
            StringAssert.Contains(await details.Content.ReadAsStringAsync(), "/Franchise/" + franchiseId);

            // Switched off, neither the series nor the link remain.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Franchises'");
            using var off = await host.SendAsync(HttpMethod.Get, "/api/v1/franchises/" + franchiseId);
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
            using var plain = await host.SendAsync(HttpMethod.Get, "/Details/" + workId);
            Assert.IsFalse((await plain.Content.ReadAsStringAsync()).Contains("/Franchise/", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Razor_HomeLibraryAndProviderTitle_RenderRealData()
        {
            await using var host = await CreateHostAsync(razor: true);

            using var home = await host.SendAsync(HttpMethod.Get, "/");
            var html = await home.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, home.StatusCode, html);
            StringAssert.Contains(html, "data-discover-recommendations=\"true\"");
            Assert.IsFalse(html.Contains("The Matrix", StringComparison.Ordinal));

            // Native recommendation route stays usable.
            using var recommendations = await host.SendAsync(HttpMethod.Get, "/?handler=Recommendations");
            html = await recommendations.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, recommendations.StatusCode, html);
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
            using var empty = await host.SendAsync(HttpMethod.Get, "/Library?type=movies&p=2");
            StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "class=\"library-paging\"");
            StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "/Library?type=movies&amp;sort=RecentlyAdded");
            using var invalid = await host.SendAsync(HttpMethod.Get, "/Library?p=2147483647");
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            StringAssert.Contains(await invalid.Content.ReadAsStringAsync(), "class=\"ui-page-error\"");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(await invalid.Content.ReadAsStringAsync(), "role=\"alert\"").Count);
            using var search = await host.SendAsync(HttpMethod.Get, "/?q=Matrix");
            StringAssert.Contains(await search.Content.ReadAsStringAsync(), "The Matrix Show");
        }

        [TestMethod]
        public async Task Discover_SearchPages_KeepLocalResultsAndRenderOnlyTheRequestedRegion()
        {
            await using var host = await CreateHostAsync(razor: true);
            await host.Fixture.RunSqlAsync("""
                WITH created AS (
                    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle")
                    SELECT 1, 'PagedOnly ' || lpad(number::text, 2, '0')
                    FROM generate_series(1, 25) AS number
                    RETURNING "Id", "CanonicalTitle"
                )
                INSERT INTO "WorkTitles" ("WorkId", "Title", "IsManual")
                SELECT "Id", "CanonicalTitle", TRUE FROM created;
                """);

            using var first = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/search", new SearchDiscoverDataV1("PagedOnly", MediaType.Movie));
            var firstItems = (await first.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!;
            Assert.AreEqual(20, firstItems.Count);
            using var second = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/search", new SearchDiscoverDataV1("PagedOnly", MediaType.Movie, page: 2));
            var secondItems = (await second.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!;
            Assert.AreEqual(5, secondItems.Count);
            Assert.IsFalse(firstItems.Select(item => item.WorkId).Intersect(secondItems.Select(item => item.WorkId)).Any());

            using var fragment = await host.SendAsync(HttpMethod.Get, "/?q=PagedOnly&type=movies&handler=Results");
            var html = await fragment.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, fragment.StatusCode, html);
            StringAssert.Contains(html, "id=\"discover-results\"");
            StringAssert.Contains(html, "q=PagedOnly&amp;p=2");
            Assert.IsFalse(html.Contains("<header", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("<script", StringComparison.Ordinal));

            using var invalid = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/search", new SearchDiscoverDataV1("PagedOnly", page: 0));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var last = await host.SendAsync(HttpMethod.Get, "/?q=PagedOnly&type=movies&p=2&handler=Results");
            Assert.IsFalse((await last.Content.ReadAsStringAsync()).Contains("data-discover-next", StringComparison.Ordinal));
            Assert.AreEqual(25L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
        }

        [TestMethod]
        [DataRow(true, "primary_release_date")]
        [DataRow(false, "first_air_date")]
        public async Task Tmdb_NewFeed_OrdersFirstReleasesAndSearchForwardsItsPage(bool movie, string dateField)
        {
            var kind = movie ? TmdbMediaKind.Movie : TmdbMediaKind.Series;
            using var handler = new FakeTmdb();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.themoviedb.org/3/") };
            var tmdb = new TmdbClient(client);
            await tmdb.ReadFeedAsync(new TmdbConnection(Token, "en-US"), kind, TmdbFeed.New, "US", 2, CancellationToken.None);
            var request = handler.Requests.Single();
            StringAssert.Contains(request.AbsolutePath, "/discover/");
            var query = QueryHelpers.ParseQuery(request.Query);
            Assert.AreEqual(dateField + ".desc", query["sort_by"].ToString());
            Assert.AreEqual(DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), query[dateField + ".lte"].ToString());
            Assert.AreEqual("2", query["page"].ToString());
            Assert.AreEqual("false", query["include_adult"].ToString());

            await tmdb.SearchAsync(new TmdbConnection(Token, "en-US"), kind, "Matrix", null, CancellationToken.None, page: 3);
            Assert.AreEqual("3", QueryHelpers.ParseQuery(handler.Requests.Last().Query)["page"].ToString());
        }

        [TestMethod]
        public async Task Rematch_AWronglyMatchedFilm_TakesTheOtherEntryAndKeepsItsFile()
        {
            await using var host = await CreateHostAsync(razor: true);
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new AdminMedia.ImportMetadataWorkDataV1(MediaType.Movie, "603"));
            var work = (await imported.Content.ReadFromJsonAsync<AdminMedia.WorkDetailsV1>())!;
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "WorkEditions" ("Id", "WorkId") VALUES (900, {work.Id});
                INSERT INTO "WorkVersions" ("Id", "WorkId", "WorkEditionId") VALUES (900, {work.Id}, 900);
                INSERT INTO "MediaAssets" ("Id", "WorkId", "WorkVersionId", "MediaAssetTypeId") VALUES (900, {work.Id}, 900, 1);
                """);
            var match = "/api/v1/admin/works/" + work.Id + "/match";

            // Only an entry of the type's provider, on the current version.
            using var wrongProvider = await host.SendAsync(HttpMethod.Post, match, new AdminMedia.RematchWorkDataV1("openlibrary", "OL1W", "The Matrix Reloaded", 2003, work.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, wrongProvider.StatusCode);
            using var stale = await host.SendAsync(HttpMethod.Post, match, new AdminMedia.RematchWorkDataV1("tmdb_movie", "604", "The Matrix Reloaded", 2003, work.Version + 5));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);

            // The Work takes the other film; the old ids go, the file stays.
            using var corrected = await host.SendAsync(HttpMethod.Post, match, new AdminMedia.RematchWorkDataV1("tmdb_movie", "604", "The Matrix Reloaded", 2003, work.Version));
            Assert.AreEqual(HttpStatusCode.OK, corrected.StatusCode, await corrected.Content.ReadAsStringAsync());
            var after = (await corrected.Content.ReadFromJsonAsync<AdminMedia.WorkDetailsV1>())!;
            Assert.AreEqual((work.Id, "The Matrix Reloaded", (int?)2003), (after.Id, after.CanonicalTitle, after.ReleaseYear));
            Assert.AreEqual("imdb:tt0234215,tmdb_movie:604", await host.ScalarAsync($"SELECT string_agg(\"Namespace\" || ':' || \"Value\", ',' ORDER BY \"Namespace\") FROM \"WorkExternalIdentities\" WHERE \"WorkId\" = {work.Id}"));
            Assert.AreEqual(work.Id, await host.ScalarAsync("SELECT \"WorkId\" FROM \"MediaAssets\" WHERE \"Id\" = 900"));
            StringAssert.Contains((string)(await host.ScalarAsync($"SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"WorkId\" = {work.Id} AND \"FactKey\" = 'Poster'"))!, "reloaded");

            // An entry another Work holds is not a correction.
            using var other = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new AdminMedia.ImportMetadataWorkDataV1(MediaType.Movie, "603"));
            Assert.AreNotEqual(work.Id, (await other.Content.ReadFromJsonAsync<AdminMedia.WorkDetailsV1>())!.Id);
            using var taken = await host.SendAsync(HttpMethod.Post, match, new AdminMedia.RematchWorkDataV1("tmdb_movie", "603", "The Matrix", 1999, after.Version));
            StringAssert.Contains(await taken.Content.ReadAsStringAsync(), "in_use");

            // The Work page searches the provider and offers the choice.
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Works/" + work.Id);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Wrong match");
            using var results = await host.SendAsync(HttpMethod.Post, "/Admin/Works/" + work.Id + "?handler=MatchSearch", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = LibraryTests.Token(html), ["MediaTypeId"] = "1", ["Query"] = "Matrix"
            });
            var list = await results.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, results.StatusCode, list);
            StringAssert.Contains(list, "Use this match");
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
            public List<Uri> Requests { get; } = [];
            private const string Movie = """{"id":603,"title":"The Matrix","original_title":"The Matrix","release_date":"1999-03-31","overview":"A hacker learns the truth.","poster_path":"/poster.jpg","backdrop_path":"/backdrop.jpg"}""";
            private const string Show = """{"id":1399,"name":"The Matrix Show","original_name":"The Matrix Show","first_air_date":"2020-01-01","overview":"A show.","poster_path":"/show.jpg"}""";

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri!);
                if (request.Headers.Authorization?.Parameter != Token) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                if (QueryHelpers.ParseQuery(request.RequestUri!.Query).GetValueOrDefault("query") == "PagedOnly")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[]}", Encoding.UTF8, "application/json") });
                }
                var body = request.RequestUri!.AbsolutePath switch
                {
                    "/3/trending/movie/week" or "/3/movie/top_rated" or "/3/discover/movie" or "/3/movie/upcoming" or "/3/search/movie" => "{\"results\":[" + Movie + "]}",
                    "/3/trending/tv/week" or "/3/tv/top_rated" or "/3/discover/tv" or "/3/search/tv" => "{\"results\":[" + Show + "]}",
                    "/3/movie/603" => Movie[..^1] + ",\"external_ids\":{\"imdb_id\":\"tt0133093\"},\"belongs_to_collection\":{\"id\":2344,\"name\":\"The Matrix Collection\"}}",
                    "/3/movie/604" => """{"id":604,"title":"The Matrix Reloaded","original_title":"The Matrix Reloaded","release_date":"2003-05-15","overview":"The fight goes on.","poster_path":"/reloaded.jpg","external_ids":{"imdb_id":"tt0234215"}}""",
                    _ => null
                };
                return Task.FromResult(body is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }
    }
}
