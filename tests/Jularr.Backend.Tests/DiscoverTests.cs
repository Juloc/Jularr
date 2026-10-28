using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Media.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Integrations.Logic.AniList;
using Jularr.Integrations.Logic.Tmdb;
using Jularr.Integrations.Logic.Tvdb;
using Jularr.Integrations.Data.Tmdb;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DiscoverTests
    {
        private const string Token = "eyJ-discover-token";
        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        public async Task Catalog_CachedRankings_RefreshKnownWorksAndStillCheckProfileAccess()
        {
            var provider = new FakeAniList();
            await using var host = await CreateHostAsync(anime: provider);
            foreach (var ranking in new[] { DiscoverFeedType.Top, DiscoverFeedType.New })
            {
                using var first = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Series, ranking, MediaSubtype.Anime));
                Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
                var shown = (await first.Content.ReadFromJsonAsync<BaseDataList<DiscoverCatalogItemV1>>())!.Single();
                Assert.AreEqual(await host.ScalarAsync("SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'anilist' AND \"Value\" = '21'"), shown.WorkId,
                    "Both rankings list the one catalog Work of the season.");
                Assert.AreEqual(MediaStateType.NotRequested, shown.MediaStateTypeId);
            }
            var requests = provider.Requests;
            provider.IsUnavailable = true;
            var workId = (long)(await host.ScalarAsync("SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'anilist' AND \"Value\" = '21'"))!;
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") VALUES ({workId}, 'Poster', '/api/v1/covers/works/{workId}/poster?v=1', TRUE);
                INSERT INTO "WantedItems" ("WorkId") VALUES ({workId});
                """);
            foreach (var ranking in new[] { DiscoverFeedType.Top, DiscoverFeedType.New })
            {
                using var cached = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Series, ranking, MediaSubtype.Anime));
                Assert.AreEqual(HttpStatusCode.OK, cached.StatusCode, await cached.Content.ReadAsStringAsync());
                var item = (await cached.Content.ReadFromJsonAsync<BaseDataList<DiscoverCatalogItemV1>>())!.Single();
                Assert.AreEqual(workId, item.WorkId);
                Assert.AreEqual(MediaStateType.Requested, item.MediaStateTypeId, "The state is the library's of now, not the one the list was kept with.");
                Assert.AreEqual($"/api/v1/covers/works/{workId}/poster?v=1", item.PosterUrl);
            }
            Assert.AreEqual(requests, provider.Requests, "Warm rankings must not ask the unavailable provider.");
            await host.Fixture.RunSqlAsync("INSERT INTO \"ProfileMediaRules\" (\"ProfileId\", \"MediaSubtypeId\", \"IsBlocked\") VALUES (101, 1, TRUE)");
            using var blocked = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Series, DiscoverFeedType.Top, MediaSubtype.Anime));
            Assert.AreEqual(HttpStatusCode.NotFound, blocked.StatusCode, await blocked.Content.ReadAsStringAsync());
            Assert.AreEqual(requests, provider.Requests);
        }

        [TestMethod]
        public async Task Catalog_OldAnimeTrending_ReadsStoredTitleWithoutProviderRefresh()
        {
            var provider = new FakeAniList();
            await using var host = await CreateHostAsync(anime: provider);
            var input = new ReadDiscoverCatalogFeedDataV1(MediaType.Series, DiscoverFeedType.Trending, MediaSubtype.Anime);
            using var first = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", input);
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
            await host.Fixture.RunSqlAsync("UPDATE \"Works\" SET \"FetchedAt\" = now() - interval '7 hours', \"CanonicalTitle\" = 'Kept stale season' WHERE \"CatalogNamespace\" = 'anilist'");
            provider.IsUnavailable = true;
            using var stale = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", input);
            Assert.AreEqual(HttpStatusCode.OK, stale.StatusCode, await stale.Content.ReadAsStringAsync());
            Assert.AreEqual("Kept stale season", (await stale.Content.ReadFromJsonAsync<BaseDataList<DiscoverCatalogItemV1>>())!.Single().Title);
            var requests = provider.Requests;
            using var cooldown = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", input);
            Assert.AreEqual(HttpStatusCode.OK, cooldown.StatusCode, await cooldown.Content.ReadAsStringAsync());
            Assert.AreEqual("Kept stale season", (await cooldown.Content.ReadFromJsonAsync<BaseDataList<DiscoverCatalogItemV1>>())!.Single().Title);
            Assert.AreEqual(requests, provider.Requests, "The provider cooldown must also apply to stale-cache reads.");
        }

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

            // The title shown is a Work already; the normal request asks for it, and its import fills the same Work.
            using var requested = await host.SendAsync(HttpMethod.Post, $"/api/v1/works/{trending.WorkId}/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            using var again = await host.SendAsync(HttpMethod.Post, $"/api/v1/works/{trending.WorkId}/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionRequests\""));
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new AdminMedia.ImportMetadataWorkDataV1(MediaType.Movie, "603"));
            Assert.AreEqual(trending.WorkId, (await imported.Content.ReadFromJsonAsync<AdminMedia.WorkDetailsV1>())!.Id);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
            Assert.AreEqual("A hacker learns the truth.", await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Overview'"));
            Assert.AreEqual("1999-03-31", await host.ScalarAsync("SELECT \"ReleasedOn\"::text FROM \"Works\""));

            using var item = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Trending));
            var details = (await item.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Single();
            Assert.AreEqual(MediaStateType.Requested, details.MediaStateTypeId);
            Assert.AreEqual(trending.WorkId, details.WorkId);

            // Search finds the local Work first and does not repeat it from TMDB.
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/search", new SearchDiscoverDataV1("Matrix"));
            var results = (await searched.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.ToArray();
            Assert.AreEqual(details.WorkId, results[0].WorkId);
            Assert.AreEqual(1, results.Count(result => result.TmdbId == 603));
            Assert.IsTrue(results.Any(result => result.MediaTypeId == MediaType.Series && result.Title == "The Matrix Show"));

            // A request without a file stays out of the default library and appears through My requests.
            await host.Fixture.RunSqlAsync("INSERT INTO \"WantedItems\" (\"WorkId\") SELECT \"Id\" FROM \"Works\" ON CONFLICT (\"WorkId\", \"GamePlatformId\") DO NOTHING");
            using var library = await host.SendAsync(HttpMethod.Get, "/api/v1/library?query=matr");
            Assert.AreEqual(HttpStatusCode.OK, library.StatusCode, await library.Content.ReadAsStringAsync());
            Assert.AreEqual(0, (await library.Content.ReadFromJsonAsync<PageResult<LibraryItemV1>>())!.Items.Count);
            using var ownRequests = await host.SendAsync(HttpMethod.Get, "/api/v1/library?query=matr&availabilityTypeIds=MyRequests");
            var entry = (await ownRequests.Content.ReadFromJsonAsync<PageResult<LibraryItemV1>>())!.Items.Single();
            Assert.AreEqual(MediaStateType.Requested, entry.MediaStateTypeId);
            Assert.AreEqual("https://image.tmdb.org/t/p/w500/poster.jpg", entry.PosterUrl);
        }

        [TestMethod]
        public async Task Catalog_KeepsWhatDiscoverLoaded_AndAnswersTrendingWithoutCheckingAge()
        {
            var tmdb = new FakeTmdb();
            await using var host = await CreateHostAsync(fake: tmdb);

            // A trending list is kept in its order; searches keep their titles too.
            using var feed = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Trending));
            Assert.AreEqual(HttpStatusCode.OK, feed.StatusCode, await feed.Content.ReadAsStringAsync());
            Assert.AreEqual(1, await host.ScalarAsync("SELECT work.\"TrendingRank\" FROM \"Works\" AS work JOIN \"WorkExternalIdentities\" AS identity ON identity.\"WorkId\" = work.\"Id\" WHERE identity.\"Namespace\" = 'tmdb_movie' AND identity.\"Value\" = '603'"));
            Assert.AreEqual("https://image.tmdb.org/t/p/w1280/backdrop.jpg", await host.ScalarAsync("SELECT work.\"BackdropUrl\" FROM \"Works\" AS work JOIN \"WorkExternalIdentities\" AS identity ON identity.\"WorkId\" = work.\"Id\" WHERE identity.\"Namespace\" = 'tmdb_movie' AND identity.\"Value\" = '603'"));
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/search", new SearchDiscoverDataV1("Matrix"));
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode);
            Assert.AreEqual("The Matrix Show", await host.ScalarAsync("SELECT work.\"CanonicalTitle\" FROM \"Works\" AS work JOIN \"WorkExternalIdentities\" AS identity ON identity.\"WorkId\" = work.\"Id\" WHERE identity.\"Namespace\" = 'tmdb_tv' AND identity.\"Value\" = '1399'"));
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT work.\"TrendingRank\" FROM \"Works\" AS work JOIN \"WorkExternalIdentities\" AS identity ON identity.\"WorkId\" = work.\"Id\" WHERE identity.\"Namespace\" = 'tmdb_tv' AND identity.\"Value\" = '1399'"));

            // While the list is fresh the catalog answers, also for a catalog of its own (AniList).
            await host.Fixture.RunSqlAsync("UPDATE \"Works\" SET \"CanonicalTitle\" = 'The Matrix (kept)' WHERE \"Id\" = (SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'tmdb_movie' AND \"Value\" = '603')");
            using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Trending));
            Assert.AreEqual("The Matrix (kept)", (await again.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Single().Title);
            using var anime = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Series, DiscoverFeedType.Trending, MediaSubtype.Anime));
            Assert.AreEqual(HttpStatusCode.OK, anime.StatusCode, await anime.Content.ReadAsStringAsync());
            Assert.AreEqual(1, await host.ScalarAsync("SELECT work.\"TrendingRank\" FROM \"Works\" AS work JOIN \"WorkExternalIdentities\" AS identity ON identity.\"WorkId\" = work.\"Id\" WHERE identity.\"Namespace\" = 'anilist' AND identity.\"Value\" = '21' AND work.\"MediaSubtypeId\" IS NOT NULL"));
            await host.Fixture.RunSqlAsync("UPDATE \"Works\" SET \"CanonicalTitle\" = 'Season 2 (kept)' WHERE \"CatalogNamespace\" = 'anilist'");
            using var animeAgain = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Series, DiscoverFeedType.Trending, MediaSubtype.Anime));
            Assert.AreEqual("Season 2 (kept)", (await animeAgain.Content.ReadFromJsonAsync<BaseDataList<DiscoverCatalogItemV1>>())!.Single().Title);
            await TimeWarmReadsAsync("AniList trending; 0 local Works, 1 result", () => host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Series, DiscoverFeedType.Trending, MediaSubtype.Anime)));

            // Existing catalog entries remain usable without any provider call, regardless of age.
            await host.Fixture.RunSqlAsync("UPDATE \"Works\" SET \"FetchedAt\" = now() - interval '7 hours', \"TrendingRank\" = 1 WHERE \"CatalogNamespace\" = 'tmdb_tv'");
            tmdb.IsUnavailable = true;
            using var stale = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Series, DiscoverFeedType.Trending));
            Assert.AreEqual(HttpStatusCode.OK, stale.StatusCode, await stale.Content.ReadAsStringAsync());
            Assert.AreEqual("The Matrix Show", (await stale.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Single().Title);
            Assert.IsFalse(tmdb.Requests.Any(request => request.AbsolutePath == "/3/trending/tv/week"));
        }

        [TestMethod]
        public async Task Catalog_WithoutTheCachingModule_KeepsNoListsAndAsksTheProviders()
        {
            var tmdb = new FakeTmdb();
            await using var host = await CreateHostAsync(fake: tmdb);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'DiscoverCache'");
            using var feed = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Trending));
            var shown = (await feed.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Single();
            Assert.AreEqual("The Matrix", shown.Title);
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/search", new SearchDiscoverDataV1("Matrix"));
            Assert.IsTrue((await searched.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Any(item => item.Title == "The Matrix Show"));
            using var anime = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Series, DiscoverFeedType.Trending, MediaSubtype.Anime));
            Assert.AreEqual(HttpStatusCode.OK, anime.StatusCode, await anime.Content.ReadAsStringAsync());

            // Every title shown is a catalog Work its card opens; no list, order or trending rank is kept, and none of them is a library title.
            Assert.AreEqual(shown.WorkId, await host.ScalarAsync("SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'tmdb_movie' AND \"Value\" = '603'"));
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"DetailedAt\" IS NULL AND \"TrendingRank\" IS NULL"));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"WorkLibraryRelations\""));
            using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Trending));
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode, await again.Content.ReadAsStringAsync());
            Assert.AreEqual(shown.WorkId, (await again.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Single().WorkId);
            Assert.AreEqual(2, tmdb.Requests.Count(request => request.AbsolutePath == "/3/trending/movie/week"));
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT count(*) FROM \"Works\""));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ExternalWorkFeeds\""));
        }

        [TestMethod]
        public async Task CacheWorker_PreparesFeedsAndRecommendations_ReadsOverlayCurrentState_AndRetainsListsOnFailure()
        {
            var tmdb = new FakeTmdb();
            await using var host = await CreateHostAsync(fake: tmdb);
            await host.Fixture.RunSqlAsync("UPDATE \"MediaTypes\" SET \"IsEnabled\" = FALSE WHERE \"Id\" <> 1");
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new AdminMedia.ImportMetadataWorkDataV1(MediaType.Movie, "603"));
            Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
            var workId = (await imported.Content.ReadFromJsonAsync<AdminMedia.WorkDetailsV1>())!.Id;
            using var requested = await host.SendAsync(HttpMethod.Post, $"/api/v1/works/{workId}/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());

            async Task Refresh()
            {
                await using var scope = host.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RefreshDiscoverFeedsV1>().ExecuteAsync<UpdateResult>(new RefreshDiscoverFeedsParametersV1(), default);
            }
            await Refresh();
            Assert.AreEqual(5L, await host.ScalarAsync("SELECT count(*) FROM \"ExternalWorkFeeds\" WHERE \"RefreshedAt\" IS NOT NULL"));
            var calls = tmdb.Requests.Count;
            await Refresh();
            Assert.AreEqual(calls, tmdb.Requests.Count, "Lists that are not due must not be fetched again.");

            using var recommendations = await host.SendAsync(HttpMethod.Post, $"/api/v1/works/{workId}/recommendations");
            Assert.AreEqual(HttpStatusCode.OK, recommendations.StatusCode, await recommendations.Content.ReadAsStringAsync());
            var recommended = (await recommendations.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Single();
            Assert.AreEqual(await host.ScalarAsync("SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'tmdb_movie' AND \"Value\" = '604'"), recommended.WorkId);
            Assert.AreEqual(MediaStateType.NotRequested, recommended.MediaStateTypeId);
            using var next = await host.SendAsync(HttpMethod.Post, $"/api/v1/works/{recommended.WorkId}/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, next.StatusCode, await next.Content.ReadAsStringAsync());
            var afterRequest = tmdb.Requests.Count;
            using var updated = await host.SendAsync(HttpMethod.Post, $"/api/v1/works/{workId}/recommendations");
            Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
            var item = (await updated.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Single();
            Assert.AreEqual(recommended.WorkId, item.WorkId);
            Assert.AreEqual(MediaStateType.Requested, item.MediaStateTypeId, "Private viewer state must be read again, not persisted with public recommendations.");
            Assert.AreEqual(afterRequest, tmdb.Requests.Count);

            await TimeWarmReadsAsync("Recommendations; 2 local Works, 1 result", () => host.SendAsync(HttpMethod.Post, $"/api/v1/works/{workId}/recommendations"));
            await TimeWarmReadsAsync("TMDB top; 2 local Works, 1 result", () => host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Top)));
            Assert.AreEqual(afterRequest, tmdb.Requests.Count);

            await host.Fixture.RunSqlAsync("UPDATE \"ExternalWorkFeeds\" SET \"RefreshedAt\" = now() - interval '13 hours'");
            tmdb.IsUnavailable = true;
            await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(Refresh);
            using var old = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Top));
            Assert.AreEqual(HttpStatusCode.OK, old.StatusCode, await old.Content.ReadAsStringAsync());
            Assert.AreEqual("The Matrix", (await old.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Single().Title);
            Assert.AreEqual(5L, await host.ScalarAsync("SELECT count(*) FROM \"ExternalWorkFeeds\" WHERE \"RefreshedAt\" IS NOT NULL"));
        }

        [TestMethod]
        public async Task FeedRefresh_DetailsTheTitlesOfEveryDiscoverListAhead_OnlyWhileTheSwitchIsOn()
        {
            var tmdb = new FakeTmdb();
            await using var host = await CreateHostAsync(fake: tmdb);
            await host.Fixture.RunSqlAsync("UPDATE \"MediaTypes\" SET \"IsEnabled\" = FALSE WHERE \"Id\" <> 1");
            async Task Refresh()
            {
                await using var scope = host.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RefreshDiscoverFeedsV1>().ExecuteAsync<UpdateResult>(new RefreshDiscoverFeedsParametersV1(), default);
            }
            int Details(string id) => tmdb.Requests.Count(request => request.AbsolutePath == "/3/movie/" + id);

            // Off: the four film lists are kept and their title stays a catalog row without details.
            await Refresh();
            Assert.AreEqual(4L, await host.ScalarAsync("SELECT count(*) FROM \"ExternalWorkFeeds\" WHERE \"RefreshedAt\" IS NOT NULL AND \"SourceValue\" IS NULL"));
            const string Matrix = "SELECT work.\"DetailedAt\" IS NOT NULL FROM \"Works\" AS work JOIN \"WorkExternalIdentities\" AS identity ON identity.\"WorkId\" = work.\"Id\" WHERE identity.\"Namespace\" = 'tmdb_movie' AND identity.\"Value\" = '603'";
            Assert.AreEqual(false, await host.ScalarAsync(Matrix));
            Assert.AreEqual(0, Details("603"));

            // On: the next pass details it once through the one detail path, although someone uses Jularr (TMDB does not wait).
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSettings\" SET \"DetailsDiscoverAhead\" = TRUE");
            await Refresh();
            Assert.AreEqual(true, await host.ScalarAsync(Matrix));
            Assert.AreEqual(1, Details("603"));
            Assert.AreEqual("A hacker learns the truth.", await host.ScalarAsync("SELECT \"Value\" FROM \"WorkMetadataFacts\" WHERE \"FactKey\" = 'Overview'"));

            // The detailed title now has recommendations; they are no Discover list and stay without details.
            await Refresh();
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ExternalWorkFeeds\" WHERE \"RefreshedAt\" IS NOT NULL AND \"SourceValue\" = '603'"));
            Assert.AreEqual(1, Details("603"));
            Assert.AreEqual(0, Details("604"));

            // A catalog that waits while Jularr is used details nothing while it is used.
            await host.Fixture.RunSqlAsync("""
                UPDATE "ExternalWorkSyncs" SET "PausesWhileUsed" = TRUE WHERE "Namespace" = 'tmdb_movie';
                UPDATE "Works" SET "DetailedAt" = NULL WHERE "Id" = (SELECT "WorkId" FROM "WorkExternalIdentities" WHERE "Namespace" = 'tmdb_movie' AND "Value" = '603');
                """);
            using var used = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/catalog/syncs");
            await Refresh();
            Assert.AreEqual(false, await host.ScalarAsync(Matrix));
            Assert.AreEqual(1, Details("603"));

            // The switch is a sub-option of the Caching module: without the module no list is kept and nothing is detailed ahead.
            await host.Fixture.RunSqlAsync("""
                UPDATE "ExternalWorkSyncs" SET "PausesWhileUsed" = FALSE;
                UPDATE "InstanceModules" SET "IsEnabled" = FALSE WHERE "Key" = 'DiscoverCache';
                """);
            await Assert.ThrowsExactlyAsync<System.Security.SecurityException>(Refresh);
            Assert.AreEqual(false, await host.ScalarAsync(Matrix));
            Assert.AreEqual(1, Details("603"));
        }

        [TestMethod]
        public async Task Work_ACatalogTitleWithoutDetails_ShowsWhatItsCatalogKeeps()
        {
            var tmdb = new FakeTmdb();
            await using var host = await CreateHostAsync(fake: tmdb);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear", "Overview", "ImageUrl", "BackdropUrl", "IsAdult", "CatalogNamespace") VALUES
                    (901, 3, 'A Book', 1965, 'A long description of the book.', 'https://img.test/book.jpg', 'https://img.test/book-wide.jpg', FALSE, 'openlibrary');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (901, 'openlibrary', 'OL1W');
                """);

            // No provider is asked: the Work, which also answers its hover preview, shows what the catalog keeps; a Work that does not exist has nothing.
            using var local = await host.SendAsync(HttpMethod.Get, "/api/v1/works/901");
            Assert.AreEqual(HttpStatusCode.OK, local.StatusCode, await local.Content.ReadAsStringAsync());
            var work = (await local.Content.ReadFromJsonAsync<Jularr.Backend.Data.User.Media.V1.WorkDetailsV1>())!;
            Assert.AreEqual(("A Book", (int?)1965), (work.CanonicalTitle, work.ReleaseYear));
            Assert.AreEqual("A long description of the book.", work.Overview);
            Assert.AreEqual(("https://img.test/book.jpg", "https://img.test/book-wide.jpg"), (work.PosterUrl, work.BackdropUrl));
            using var unknown = await host.SendAsync(HttpMethod.Get, "/api/v1/works/999999");
            Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.AreEqual(0, tmdb.Requests.Count);

            // A fact of the Work's own comes before the catalog's text.
            await host.Fixture.RunSqlAsync("INSERT INTO \"WorkMetadataFacts\" (\"WorkId\", \"FactKey\", \"Value\", \"IsManual\") VALUES (901, 'Overview', 'Written by a person.', TRUE)");
            using var edited = await host.SendAsync(HttpMethod.Get, "/api/v1/works/901");
            Assert.AreEqual("Written by a person.", (await edited.Content.ReadFromJsonAsync<Jularr.Backend.Data.User.Media.V1.WorkDetailsV1>())!.Overview);
        }

        [TestMethod]
        public async Task Feed_AProviderThatJustFailed_IsNotAskedAgainAtOnce()
        {
            var tmdb = new FakeTmdb { IsUnavailable = true };
            await using var host = await CreateHostAsync(fake: tmdb);
            using var first = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Top));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, first.StatusCode);
            var asked = tmdb.Requests.Count;
            Assert.IsTrue(asked > 0);

            // The next shelves of the page do not wait for it again, even while it answers again; the memory is short.
            tmdb.IsUnavailable = false;
            using var second = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.New));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, second.StatusCode);
            Assert.AreEqual(asked, tmdb.Requests.Count);
        }

        [TestMethod]
        public async Task Catalog_ReadWhole_TrendingTiesKeepExactPageBoundaries()
        {
            await using var host = await CreateHostAsync();
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" = 'tmdb_movie';
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ImageUrl", "TrendingRank", "CatalogNamespace")
                SELECT 1000 + n, 1, 'Tied title ' || n, 'https://img.test/' || n || '.jpg', CASE WHEN n <= 30 THEN 1 ELSE 2 END, 'tmdb_movie'
                FROM generate_series(1, 45) n ORDER BY n DESC;
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") SELECT 1000 + n, 'tmdb_movie', n::text FROM generate_series(1, 45) n;
                """);
            for (var page = 1; page <= 4; page++)
            {
                using var response = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Trending, page));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                var rows = (await response.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!;
                var expected = Enumerable.Range((page - 1) * 20 + 1, Math.Max(0, Math.Min(20, 45 - (page - 1) * 20))).ToArray();
                CollectionAssert.AreEqual(expected, rows.Select(row => row.TmdbId).ToArray());
            }
        }

        [TestMethod]
        public async Task Catalog_ReadWhole_PreservesIdentityStateOwnedCoverAndBlockedWorkScope()
        {
            await using var host = await CreateHostAsync();
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" = 'anilist';
                INSERT INTO "ExternalWorkSyncs" ("Namespace", "RequestsPerMinute", "CompletedAt") VALUES ('openlibrary', 20, now());
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle") VALUES
                    (100, 3, NULL, 'Owned book'), (101, 3, 3, 'Blocked novel'), (102, 2, 1, 'Anime work'), (103, 2, 1, 'Anime season');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (100, 102, 1, 1), (101, 103, 1, 1);
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (100, 'openlibrary', 'owned'), (101, 'openlibrary', 'blocked'), (102, 'anilist', 'direct');
                INSERT INTO "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value") VALUES (100, 'anilist', 'direct'), (101, 'anilist', 'season');
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") VALUES
                    (100, 'Poster', '/api/v1/covers/works/100/poster?v=1', TRUE), (101, 'Poster', '/api/v1/covers/works/101/poster?v=1', TRUE),
                    (102, 'Poster', '/api/v1/covers/works/102/poster?v=1', TRUE), (103, 'Poster', '/api/v1/covers/works/103/poster?v=1', TRUE);
                INSERT INTO "WantedItems" ("WorkId") VALUES (100), (102), (103);
                INSERT INTO "ProfileMediaRules" ("ProfileId", "MediaSubtypeId", "IsBlocked") VALUES (101, 3, TRUE);
                UPDATE "Works" SET "CatalogNamespace" = 'openlibrary', "ImageUrl" = 'https://img.test/owned.jpg', "TrendingRank" = 1 WHERE "Id" = 100;
                UPDATE "Works" SET "CatalogNamespace" = 'openlibrary', "ImageUrl" = 'https://img.test/blocked.jpg', "TrendingRank" = 2 WHERE "Id" = 101;
                UPDATE "Works" SET "CatalogNamespace" = 'anilist', "ImageUrl" = 'https://img.test/direct.jpg', "TrendingRank" = 1 WHERE "Id" = 102;
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ImageUrl", "TrendingRank", "CatalogNamespace") VALUES (104, 3, 'Unknown book', 'https://img.test/unknown.jpg', 3, 'openlibrary');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (104, 'openlibrary', 'unknown');
                """);
            using var books = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Book, DiscoverFeedType.Trending));
            Assert.AreEqual(HttpStatusCode.OK, books.StatusCode, await books.Content.ReadAsStringAsync());
            var bookItems = (await books.Content.ReadFromJsonAsync<BaseDataList<DiscoverCatalogItemV1>>())!;
            CollectionAssert.AreEqual(new long?[] { 100, 104 }, bookItems.Select(item => item.WorkId).ToArray(), "The blocked light novel is no book of the profile's shelf.");
            Assert.AreEqual(MediaStateType.Requested, bookItems[0].MediaStateTypeId);
            Assert.AreEqual("/api/v1/covers/works/100/poster?v=1", bookItems[0].PosterUrl);
            Assert.AreEqual(MediaStateType.NotRequested, bookItems[1].MediaStateTypeId);
            Assert.AreEqual("https://img.test/unknown.jpg", bookItems[1].PosterUrl);

            using var anime = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Series, DiscoverFeedType.Trending, MediaSubtype.Anime));
            Assert.AreEqual(HttpStatusCode.OK, anime.StatusCode, await anime.Content.ReadAsStringAsync());
            var animeItems = (await anime.Content.ReadFromJsonAsync<BaseDataList<DiscoverCatalogItemV1>>())!;
            Assert.AreEqual(102L, animeItems[0].WorkId, "A Work and its season carrying the same provider identity must not duplicate the card.");
            Assert.AreEqual(1, animeItems.Count(item => item.WorkId == 102));
            Assert.AreEqual("/api/v1/covers/works/102/poster?v=1", animeItems[0].PosterUrl);
        }

        [TestMethod]
        public async Task Catalog_ReadWhole_AnswersEveryShelfAndSuggestsWhileTyping()
        {
            var tmdb = new FakeTmdb();
            await using var host = await CreateHostAsync(fake: tmdb);
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" IN ('tmdb_movie', 'anilist');
                INSERT INTO "Works" ("Id", "CatalogNamespace", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleasedOn", "ImageUrl", "Popularity", "Rating", "Votes", "TrendingRank", "IsAdult") VALUES
                    (603, 'tmdb_movie', 1, NULL, 'The Matrix', '1999-03-31', 'https://img.test/603.jpg', 80, 8.7, 25000, 1, FALSE),
                    (604, 'tmdb_movie', 1, NULL, 'The Matrix Reloaded', '2003-05-15', 'https://img.test/604.jpg', 60, 7.0, 10000, NULL, FALSE),
                    (700, 'tmdb_movie', 1, NULL, 'Rated By Five', '2020-01-01', 'https://img.test/700.jpg', 1, 9.9, 5, NULL, FALSE),
                    (701, 'tmdb_movie', 1, NULL, 'Out Now', current_date - 10, 'https://img.test/701.jpg', 50, NULL, NULL, NULL, FALSE),
                    (702, 'tmdb_movie', 1, NULL, 'Out Soon', current_date + 30, 'https://img.test/702.jpg', 40, NULL, NULL, NULL, FALSE),
                    (703, 'tmdb_movie', 1, NULL, 'Matrix Adult', '2001-01-01', 'https://img.test/703.jpg', 90, 9.0, 9000, 2, TRUE),
                    (704, 'tmdb_movie', 1, NULL, 'Matrix Unread', NULL, NULL, 99, NULL, NULL, 3, FALSE),
                    (9021, 'anilist', 2, 1, 'The Matrix Anime', '2021-04-01', 'https://img.test/21.jpg', 5000, 8.0, 5000, 1, FALSE);
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES
                    (603, 'tmdb_movie', '603'), (604, 'tmdb_movie', '604'), (700, 'tmdb_movie', '700'), (701, 'tmdb_movie', '701'), (702, 'tmdb_movie', '702'), (703, 'tmdb_movie', '703'),
                    (704, 'tmdb_movie', '704'), (9021, 'anilist', '21'), (603, 'imdb', 'tt0133093'), (604, 'imdb', 'tt0234215');
                """);

            // The whole catalogs answer all their shelves at once; series still come from TMDB and are not among them.
            using var read = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/shelves", new ReadDiscoverShelvesDataV1());
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            var shelves = (await read.Content.ReadFromJsonAsync<BaseDataList<DiscoverShelfV1>>())!.ToArray();
            string Shelf(string scope, DiscoverFeedType feed) => string.Join(",", shelves.Single(shelf => shelf.ScopeSlug == scope && shelf.DiscoverFeedTypeId == feed) is var found
                ? found.Items.Select(item => item.TmdbId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Concat(found.CatalogItems.Select(item => item.ExternalId)) : []);
            Assert.AreEqual("603", Shelf("movies", DiscoverFeedType.Trending));
            Assert.AreEqual("603,604", Shelf("movies", DiscoverFeedType.Top));
            Assert.AreEqual("701", Shelf("movies", DiscoverFeedType.New));
            Assert.AreEqual("702", Shelf("movies", DiscoverFeedType.Upcoming));
            Assert.AreEqual("21", Shelf("anime", DiscoverFeedType.Trending));
            Assert.IsFalse(shelves.Any(shelf => shelf.ScopeSlug == "series"));

            // The rating of the catalog travels with its title; a title without one carries none.
            Assert.AreEqual(8.7m, shelves.Single(shelf => shelf.ScopeSlug == "movies" && shelf.DiscoverFeedTypeId == DiscoverFeedType.Trending).Items.Single().Rating);
            Assert.IsNull(shelves.Single(shelf => shelf.ScopeSlug == "movies" && shelf.DiscoverFeedTypeId == DiscoverFeedType.New).Items.Single().Rating);
            Assert.AreEqual(8.0m, shelves.Single(shelf => shelf.ScopeSlug == "anime" && shelf.DiscoverFeedTypeId == DiscoverFeedType.Trending).CatalogItems.Single().Rating);
            Assert.IsFalse(tmdb.Requests.Any(request => request.AbsolutePath.StartsWith("/3/trending/movie", StringComparison.Ordinal)));

            // A browse page goes on in the catalog; a requested title carries its state.
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/works/603/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            using var top = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Top));
            var topItems = (await top.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.ToArray();
            Assert.AreEqual(MediaStateType.Requested, topItems[0].MediaStateTypeId);
            Assert.AreEqual(8.7m, topItems[0].Rating);
            Assert.AreEqual(7.0m, topItems[1].Rating);
            CollectionAssert.AreEqual(new long[] { 603, 604 }, topItems.Select(item => item.WorkId).ToArray());
            using var nextPage = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Top, 2));
            Assert.AreEqual(0, (await nextPage.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Count);

            // Typed words suggest the library's title first, then the others; adult and unread titles stay out.
            using var suggested = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/suggestions", new SuggestDiscoverDataV1("matr"));
            Assert.AreEqual(HttpStatusCode.OK, suggested.StatusCode, await suggested.Content.ReadAsStringAsync());
            var suggestions = (await suggested.Content.ReadFromJsonAsync<BaseDataList<DiscoverSuggestionV1>>())!.ToArray();
            Assert.AreEqual("The Matrix|The Matrix Anime|The Matrix Reloaded", string.Join("|", suggestions.Select(item => item.Title)));
            Assert.AreEqual("movies", suggestions[0].ScopeSlug);
            CollectionAssert.AreEqual(new long[] { 603, 9021, 604 }, suggestions.Select(item => item.WorkId).ToArray());
            Assert.AreEqual("anime", suggestions[1].ScopeSlug);
            using var narrowed = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/suggestions", new SuggestDiscoverDataV1("matrix rel", "movies"));
            Assert.AreEqual("The Matrix Reloaded", (await narrowed.Content.ReadFromJsonAsync<BaseDataList<DiscoverSuggestionV1>>())!.Single().Title);

            // Without full local discovery the shelves come from the providers again; without the Caching module nothing is suggested.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'LocalDiscovery'");
            using var off = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/shelves", new ReadDiscoverShelvesDataV1());
            Assert.AreEqual(0, (await off.Content.ReadFromJsonAsync<BaseDataList<DiscoverShelfV1>>())!.Count);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'DiscoverCache'");
            using var none = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/suggestions", new SuggestDiscoverDataV1("matr"));
            Assert.AreEqual(0, (await none.Content.ReadFromJsonAsync<BaseDataList<DiscoverSuggestionV1>>())!.Count);
        }

        [TestMethod]
        public async Task Franchise_TmdbCollection_ListsTheVisibleTitles()
        {
            await using var host = await CreateHostAsync(razor: true);
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new AdminMedia.ImportMetadataWorkDataV1(MediaType.Movie, "603"));
            Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
            var workId = (await imported.Content.ReadFromJsonAsync<AdminMedia.WorkDetailsV1>())!.Id;
            using var requested = await host.SendAsync(HttpMethod.Post, $"/api/v1/works/{workId}/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var franchiseId = (long)(await host.ScalarAsync("SELECT \"FranchiseId\" FROM \"Works\" WHERE \"Id\" = " + workId))!;
            Assert.AreEqual("The Matrix Collection", await host.ScalarAsync("SELECT franchise.\"Name\" FROM \"Franchises\" AS franchise JOIN \"FranchiseExternalIdentities\" AS identity ON identity.\"FranchiseId\" = franchise.\"Id\" WHERE identity.\"Namespace\" = 'tmdb' AND identity.\"Value\" = '2344'"));

            // The series lists its titles and the details link to it.
            using var series = await host.SendAsync(HttpMethod.Get, "/api/v1/franchises/" + franchiseId + "/works");
            Assert.AreEqual(HttpStatusCode.OK, series.StatusCode, await series.Content.ReadAsStringAsync());
            Assert.AreEqual(workId, (await series.Content.ReadFromJsonAsync<PageResult<LibraryItemV1>>())!.Items.Single().Id);
            using var page = await host.SendAsync(HttpMethod.Get, "/Franchise/" + franchiseId);
            StringAssert.Contains(await page.Content.ReadAsStringAsync(), "The Matrix Collection");
            using var details = await host.SendAsync(HttpMethod.Get, "/Details/" + workId);
            StringAssert.Contains(await details.Content.ReadAsStringAsync(), "/Franchise/" + franchiseId);

            // With another title in the series, the details show the series as related works.
            await host.Fixture.RunSqlAsync("INSERT INTO \"Works\" (\"MediaTypeId\", \"CanonicalTitle\", \"ReleaseYear\", \"FranchiseId\") VALUES (1, 'The Matrix Revolutions', 2003, " + franchiseId + ")");
            using var related = await host.SendAsync(HttpMethod.Get, "/Details/" + workId);
            var relatedHtml = await related.Content.ReadAsStringAsync();
            StringAssert.Contains(relatedHtml, "The Matrix Revolutions");
            StringAssert.Contains(relatedHtml, "href=\"/Franchise/" + franchiseId + "\"");

            // Switched off, neither the series nor the link remain.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Franchises'");
            using var off = await host.SendAsync(HttpMethod.Get, "/api/v1/franchises/" + franchiseId);
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
            using var plain = await host.SendAsync(HttpMethod.Get, "/Details/" + workId);
            Assert.IsFalse((await plain.Content.ReadAsStringAsync()).Contains("/Franchise/", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Details_TmdbMovie_ShowsLengthGenresTrailerAndPeople()
        {
            await using var host = await CreateHostAsync(razor: true);
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new AdminMedia.ImportMetadataWorkDataV1(MediaType.Movie, "603"));
            Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
            var workId = (await imported.Content.ReadFromJsonAsync<AdminMedia.WorkDetailsV1>())!.Id;

            // Cast before crew; crew keeps only key jobs.
            using var credits = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + workId + "/credits");
            Assert.AreEqual(HttpStatusCode.OK, credits.StatusCode, await credits.Content.ReadAsStringAsync());
            var people = (await credits.Content.ReadFromJsonAsync<BaseDataList<WorkCreditV1>>())!;
            CollectionAssert.AreEqual(new[] { "Keanu Reeves", "Lana Wachowski" }, people.Select(person => person.Name).ToArray());
            Assert.AreEqual(("https://image.tmdb.org/t/p/w185/neo.jpg", "Neo", PersonRole.Actor), (people[0].ImageUrl, people[0].Credit, people[0].PersonRoleId));
            Assert.AreEqual((PersonRole.Director, "Director"), (people[1].PersonRoleId, people[1].Credit));

            // A new import keeps each person, found again by their TMDB id.
            using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new AdminMedia.ImportMetadataWorkDataV1(MediaType.Movie, "603"));
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode, await again.Content.ReadAsStringAsync());
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"People\""));
            Assert.AreEqual(people[0].PersonId, await host.ScalarAsync("SELECT \"PersonId\" FROM \"PersonExternalIdentities\" WHERE \"Namespace\" = 'tmdb' AND \"Value\" = '6384'"));

            using var details = await host.SendAsync(HttpMethod.Get, "/Details/" + workId);
            var html = await details.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, details.StatusCode, html);
            StringAssert.Contains(html, "136 min");
            StringAssert.Contains(html, "Action, Science Fiction");
            StringAssert.Contains(html, "Village Roadshow");
            StringAssert.Contains(html, "https://www.youtube.com/watch?v=vKQi3bBA1y8");
            StringAssert.Contains(html, "Neo");
            StringAssert.Contains(html, "Director");

            // Similar titles from TMDB are catalog Works and lead to their page.
            StringAssert.Contains(html, "The Matrix Reloaded");
            var similar = (long)(await host.ScalarAsync("SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'tmdb_movie' AND \"Value\" = '604'"))!;
            StringAssert.Contains(html, "href=\"/Details/" + similar + "\"");
            Assert.IsFalse(html.Contains("/Discover/movies/", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Anime_AniListSeason_IsItsOwnWorkUntilASeriesMapsIt_ThenOpensTheSeriesAtThatSeason()
        {
            await using var host = await CreateHostAsync(razor: true);

            // The backend names the scopes of the media-type bar, anime among the catalogs.
            using var scopes = await host.SendAsync(HttpMethod.Get, "/api/v1/discover/scopes");
            var anime = (await scopes.Content.ReadFromJsonAsync<BaseDataList<DiscoverScopeV1>>())!.Single(scope => scope.Slug == "anime");
            Assert.AreEqual((MediaType.Series, (MediaSubtype?)MediaSubtype.Anime, true), (anime.MediaTypeId, anime.MediaSubtypeId, anime.IsCatalog));
            CollectionAssert.AreEqual(new[] { DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New }, anime.Feeds);

            // The anime tab ranks AniList seasons; a season no series holds yet is a catalog Work of its own and opens its page and preview.
            using var tab = await host.SendAsync(HttpMethod.Get, "/?type=anime&handler=Recommendations");
            var html = await tab.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, tab.StatusCode, html);
            StringAssert.Contains(html, "The Matrix Show Season 2");
            var season = (long)(await host.ScalarAsync("SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'anilist' AND \"Value\" = '21'"))!;
            StringAssert.Contains(html, "href=\"/Details/" + season + "\"");
            StringAssert.Contains(html, "data-preview-src=\"/MediaPreview?work=" + season + "\"");
            Assert.IsFalse(html.Contains("/Discover/anime/", StringComparison.Ordinal));
            using var own = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + season);
            var shown = (await own.Content.ReadFromJsonAsync<Jularr.Backend.Data.User.Media.V1.WorkDetailsV1>())!;
            Assert.AreEqual(("The Matrix Show Season 2", "Season two.", "https://img.anili.st/second.jpg"), (shown.CanonicalTitle, shown.Overview, shown.PosterUrl));

            // Once the series maps the season, the season is that series, opened at the season.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear", "DetailedAt") VALUES (950, 2, 1, 'The Matrix Show', 2020, now());
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (950, 'tmdb_tv', '1399');
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (950, 950, 1, 1), (951, 950, 1, 2);
                INSERT INTO "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value") VALUES (951, 'anilist', '21');
                """);
            using var found = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/search", new SearchDiscoverCatalogDataV1(MediaType.Series, "Matrix", MediaSubtype.Anime));
            Assert.AreEqual(HttpStatusCode.OK, found.StatusCode, await found.Content.ReadAsStringAsync());
            var series = (await found.Content.ReadFromJsonAsync<BaseDataList<DiscoverCatalogItemV1>>())!.Single();
            Assert.AreEqual((950L, (int?)2, "21"), (series.WorkId, series.SeasonNumber, series.ExternalId));
            using var search = await host.SendAsync(HttpMethod.Get, "/?type=anime&q=Matrix");
            html = await search.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, search.StatusCode, html);
            StringAssert.Contains(html, "href=\"/Details/950?season=2\"");
            using var opened = await host.SendAsync(HttpMethod.Get, "/Details/950?season=2");
            Assert.AreEqual(HttpStatusCode.OK, opened.StatusCode, await opened.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task RelatedWork_RequestedFromItsRow_IsTheNormalRequestWithoutLeavingThePage()
        {
            await using var host = await CreateHostAsync(razor: true, anime: new FakeAniList { FindsFirstSeason = true }, tvdb: new FakeTvdb());

            // A Work names a season of an anime that is only a catalog title.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (700, 1, 'Source Film', 2001);
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear", "CatalogNamespace") VALUES (800, 2, 1, 'The Matrix Show Season 2', 2021, 'anilist');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (800, 'anilist', '21');
                INSERT INTO "WorkRelations" ("WorkId", "RelatedWorkId", "WorkRelationTypeId", "SourceKey") VALUES (700, 800, 1, 'anilist');
                """);
            using var browser = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
            browser.DefaultRequestHeaders.Add("Cookie", string.Join("; ", host.Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
            browser.DefaultRequestHeaders.Add("HX-Request", "true");

            // The row leads to the Work and offers the request itself.
            using var shown = await browser.GetAsync("/Details/700?tab=related");
            var html = await shown.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, shown.StatusCode, html);
            StringAssert.Contains(html, "href=\"/Details/800\"");
            StringAssert.Contains(html, "state-chip__label\">Not in library<");
            StringAssert.Contains(html, "handler=RequestRelated");
            StringAssert.Contains(html, "name=\"relatedWorkId\" value=\"800\"");
            var token = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
            var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["relatedWorkId"] = "800" };

            // A title this Work does not name is not requested through it.
            using var foreign = await browser.PostAsync("/Details/700?handler=RequestRelated&tab=related", new FormUrlEncodedContent(new Dictionary<string, string>(form) { ["relatedWorkId"] = "999" }));
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionRequests\""));

            // The request is the normal one of the related Work, an anime season of its own is requested as its series, which takes the season in; the answer is the page
            // again with the hint and no redirect, and the row shows the series' state instead of the button.
            using var requested = await browser.PostAsync("/Details/700?handler=RequestRelated&tab=related", new FormUrlEncodedContent(form));
            var answer = await requested.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, answer);
            StringAssert.Contains(string.Join(' ', requested.Headers.GetValues("HX-Trigger")), "Requested");
            var series = (long)(await host.ScalarAsync("SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'tmdb_tv' AND \"Value\" = '1399'"))!;
            Assert.AreEqual(1L, await host.ScalarAsync($"SELECT count(*) FROM \"AcquisitionRequests\" WHERE \"WorkId\" = {series}"));
            Assert.AreEqual(series, await host.ScalarAsync("SELECT \"RelatedWorkId\" FROM \"WorkRelations\" WHERE \"WorkId\" = 700"));
            Assert.IsFalse(answer.Contains("state-chip__label\">Not in library<", StringComparison.Ordinal));
            Assert.IsFalse(answer.Contains("name=\"relatedWorkId\" value=\"800\"", StringComparison.Ordinal));

            // Without Requests there is no button and no way to post one.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Requests';");
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear", "CatalogNamespace") VALUES (801, 2, 1, 'Another Season', 2022, 'anilist');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (801, 'anilist', '22');
                INSERT INTO "WorkRelations" ("WorkId", "RelatedWorkId", "WorkRelationTypeId", "SourceKey") VALUES (700, 801, 2, 'anilist');
                """);
            using var disabled = await browser.GetAsync("/Details/700?tab=related");
            Assert.IsFalse((await disabled.Content.ReadAsStringAsync()).Contains("handler=RequestRelated", StringComparison.Ordinal));
            using var refused = await browser.PostAsync("/Details/700?handler=RequestRelated&tab=related", new FormUrlEncodedContent(new Dictionary<string, string>(form) { ["relatedWorkId"] = "801" }));
            Assert.AreNotEqual(HttpStatusCode.OK, refused.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionRequests\""));
        }

        [TestMethod]
        public async Task Razor_HomeLibraryAndProviderTitle_RenderRealData()
        {
            await using var host = await CreateHostAsync(razor: true);

            using var home = await host.SendAsync(HttpMethod.Get, "/");
            var html = await home.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, home.StatusCode, html);
            StringAssert.Contains(html, "data-discover-recommendations=\"true\"");
            StringAssert.Contains(html, "The Matrix");
            StringAssert.Contains(html, "hero-carousel");

            // Native recommendation route stays usable, but no longer inserts Hero content.
            using var recommendations = await host.SendAsync(HttpMethod.Get, "/?handler=Recommendations");
            html = await recommendations.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, recommendations.StatusCode, html);
            StringAssert.Contains(html, "The Matrix");
            StringAssert.Contains(html, "class=\"hero-carousel");
            var workId = (long)(await host.ScalarAsync("SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'tmdb_movie' AND \"Value\" = '603'"))!;
            StringAssert.Contains(html, "href=\"/Details/" + workId + "\"");
            Assert.IsFalse(html.Contains("/Discover/movies/", StringComparison.Ordinal));

            using var browse = await host.SendAsync(HttpMethod.Get, "/?type=series&view=Top");
            var browseHtml = await browse.Content.ReadAsStringAsync();
            StringAssert.Contains(browseHtml, "The Matrix Show");

            // A ranking names itself and leads back to its type; the page keeps the one capsule bar and no row of tabs.
            StringAssert.Contains(browseHtml, "<h2>Top rated</h2>");
            StringAssert.Contains(browseHtml, "href=\"/?type=series\"");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(browseHtml, "class=\"capsule-bar__areas\"").Count);
            Assert.AreEqual(0, System.Text.RegularExpressions.Regex.Matches(browseHtml, "class=\"navigation-tabs[ \"]").Count);

            // The title's page is the Work's; its first open brings the details from the catalog, and it requests the title as any Work.
            using var title = await host.SendAsync(HttpMethod.Get, "/Details/" + workId);
            html = await title.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, title.StatusCode, html);
            StringAssert.Contains(html, "Keanu Reeves");
            StringAssert.Contains(html, "handler=Request\"");
            using var requested = await host.SendAsync(HttpMethod.Post, "/Details/" + workId + "?handler=Request", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = LibraryTests.Token(html), ["UiLocaleId"] = "1"
            });
            Assert.AreEqual(HttpStatusCode.Redirect, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionRequests\" WHERE \"WorkId\" = " + workId));

            using var library = await host.SendAsync(HttpMethod.Get, "/Library?type=movies");
            Assert.AreEqual(HttpStatusCode.OK, library.StatusCode);
            using var empty = await host.SendAsync(HttpMethod.Get, "/Library?type=movies&p=2");
            StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "data-infinite-list=\".library-grid\"");
            StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "data-infinite-page=\"2\"");
            StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "href=\"/Library?type=movies\"");
            using var invalid = await host.SendAsync(HttpMethod.Get, "/Library?p=2147483647");
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            StringAssert.Contains(await invalid.Content.ReadAsStringAsync(), "class=\"ui-page-error\"");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(await invalid.Content.ReadAsStringAsync(), "role=\"alert\"").Count);
            using var search = await host.SendAsync(HttpMethod.Get, "/?q=Matrix");
            StringAssert.Contains(await search.Content.ReadAsStringAsync(), "The Matrix Show");
        }

        [TestMethod]
        public async Task Razor_Cards_OfferTheHoverPreview_AndLibraryShowsTheSharedBars()
        {
            var tmdb = new FakeTmdb();
            await using var host = await CreateHostAsync(razor: true, fake: tmdb);

            // A card names the Work its delayed hover preview comes from, and nothing is requested before the hover.
            using var recommendations = await host.SendAsync(HttpMethod.Get, "/?handler=Recommendations");
            var html = await recommendations.Content.ReadAsStringAsync();
            var workId = (long)(await host.ScalarAsync("SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'tmdb_movie' AND \"Value\" = '603'"))!;
            StringAssert.Contains(html, "data-preview-src=\"/MediaPreview?work=" + workId + "\"");
            Assert.IsFalse(html.Contains("youtube", StringComparison.OrdinalIgnoreCase));

            // A catalog title without details describes itself from its catalog entry; no provider is asked.
            var asked = tmdb.Requests.Count;
            using var described = await host.SendAsync(HttpMethod.Get, "/MediaPreview?work=" + workId);
            html = await described.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, described.StatusCode, html);
            StringAssert.Contains(html, "data-preview-mode=\"description\"");
            StringAssert.Contains(html, "A hacker learns the truth.");
            StringAssert.Contains(html, "href=\"/Details/" + workId + "\"");
            Assert.AreEqual(asked, tmdb.Requests.Count);

            // The fragment brings the description and the Work's trailer; a malformed key is never embedded.
            await host.Fixture.RunSqlAsync($"INSERT INTO \"WorkMetadataFacts\" (\"WorkId\", \"FactKey\", \"Value\", \"IsManual\") VALUES ({workId}, 'Trailer', 'vKQi3bBA1y8', TRUE)");
            using var preview = await host.SendAsync(HttpMethod.Get, "/MediaPreview?work=" + workId);
            html = await preview.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode, html);
            StringAssert.Contains(html, "class=\"media-card__preview\"");
            StringAssert.Contains(html, "data-preview-mode=\"trailer\"");
            StringAssert.Contains(html, "data-trailer-id=\"vKQi3bBA1y8\"");
            StringAssert.Contains(html, "data-trailer-action=\"toggle\"");
            StringAssert.Contains(html, "data-trailer-action=\"mute\"");
            StringAssert.Contains(html, "A hacker learns the truth.");
            await host.Fixture.RunSqlAsync($"UPDATE \"WorkMetadataFacts\" SET \"Value\" = 'not a key\"' WHERE \"WorkId\" = {workId} AND \"FactKey\" = 'Trailer'");
            using var malformed = await host.SendAsync(HttpMethod.Get, "/MediaPreview?work=" + workId);
            StringAssert.Contains(await malformed.Content.ReadAsStringAsync(), "data-preview-mode=\"description\"");

            // While the provider cannot answer the Work's own description remains; a Work that does not exist has no preview.
            tmdb.IsUnavailable = true;
            using var offline = await host.SendAsync(HttpMethod.Get, "/MediaPreview?work=" + workId);
            Assert.AreEqual(HttpStatusCode.OK, offline.StatusCode);
            StringAssert.Contains(await offline.Content.ReadAsStringAsync(), "A hacker learns the truth.");
            using var unknown = await host.SendAsync(HttpMethod.Get, "/MediaPreview?work=999999");
            Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
            using var invalid = await host.SendAsync(HttpMethod.Get, "/MediaPreview");
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);

            // Library, like Favorites and Discover, has the one media-type bar, a filter panel, the order and the layout.
            using var library = await host.SendAsync(HttpMethod.Get, "/Library");
            html = await library.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, library.StatusCode, html);
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(html, "class=\"capsule-bar__areas\"").Count);
            StringAssert.Contains(html, "id=\"library-filter\"");
            StringAssert.Contains(html, "data-drawer-open=\"library-filter\"");
            StringAssert.Contains(html, "data-layout-toggle");
            StringAssert.Contains(html, "name=\"year\"");
            StringAssert.Contains(html, "name=\"sort\"");
            using var narrowed = await host.SendAsync(HttpMethod.Get, "/Library?type=movies,series&year=2020&sort=Year");
            Assert.AreEqual(HttpStatusCode.OK, narrowed.StatusCode);
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
        public async Task Film_ShowsItsTitleAndSummaryInTheProfilesLanguage()
        {
            await using var host = await CreateHostAsync(razor: true);
            using var imported = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works/metadata", new AdminMedia.ImportMetadataWorkDataV1(MediaType.Movie, "603"));
            var work = (await imported.Content.ReadFromJsonAsync<AdminMedia.WorkDetailsV1>())!;

            // A German profile reads TMDB's German title and summary, the one of its own region first; an English one keeps the original.
            Assert.AreEqual("en", await host.ScalarAsync("SELECT \"OriginalLanguage\" FROM \"Works\" WHERE \"Id\" = " + work.Id));
            foreach (var (locale, title, summary) in new[] { ("de-DE", "Matrix", "Ein Hacker erfährt die Wahrheit."), ("en-GB", "The Matrix", null) })
            {
                await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"UiLocaleId\" = (SELECT \"Id\" FROM \"UiLocales\" WHERE \"Locale\" = '" + locale + "') WHERE \"Id\" = 101");
                using var details = await host.SendAsync(HttpMethod.Get, "/api/v1/works/" + work.Id);
                var shown = (await details.Content.ReadFromJsonAsync<Jularr.Backend.Data.User.Media.V1.WorkDetailsV1>())!;
                Assert.AreEqual(title, shown.CanonicalTitle, locale);
                if (summary is not null) Assert.AreEqual(summary, shown.Overview, locale);
                else Assert.AreNotEqual("Ein Hacker erfährt die Wahrheit.", shown.Overview, locale);
            }
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
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Works/" + work.Id + "?Tab=Metadata");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Wrong match");
            using var results = await host.SendAsync(HttpMethod.Post, "/Admin/Works/" + work.Id + "?Tab=Metadata&handler=MatchSearch", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = LibraryTests.Token(html), ["MediaTypeId"] = "1", ["Query"] = "Matrix"
            });
            var list = await results.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, results.StatusCode, list);
            StringAssert.Contains(list, "Use this match");
        }

        private async Task TimeWarmReadsAsync(string name, Func<Task<HttpResponseMessage>> request)
        {
            if (Environment.GetEnvironmentVariable("JULARR_TEST_CACHE_TIMING") != "1") return;
            async Task<double[]> Measure(int count)
            {
                var samples = new double[count];
                for (var i = 0; i < count; i++)
                {
                    var watch = global::System.Diagnostics.Stopwatch.StartNew();
                    using var response = await request();
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                    samples[i] = watch.Elapsed.TotalMilliseconds;
                }
                return samples;
            }
            foreach (var concurrency in new[] { 1, 4 })
            {
                var samples = (await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Measure(100 / concurrency)))).SelectMany(values => values).Order().ToArray();
                TestContext.WriteLine($"Cache timing {name}: pooling enabled; 100 authenticated in-process HTTP requests; concurrency={concurrency}; P50={samples[49]:F1}ms; P95={samples[94]:F1}ms; Max={samples[^1]:F1}ms.");
            }
        }

        [TestMethod]
        public async Task Details_FirstOpen_ImportsOnceIntoTheSameWork_ProviderDownShowsTheCatalogRow_AndStaleDetailsRefreshLater()
        {
            var tmdb = new FakeTmdb();
            await using var host = await CreateHostAsync(razor: true, fake: tmdb);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear", "Overview", "CatalogNamespace") VALUES (960, 1, 'The Matrix', 1999, 'Catalog text.', 'tmdb_movie');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (960, 'tmdb_movie', '603');
                """);
            int Imports() => tmdb.Requests.Count(request => request.AbsolutePath == "/3/movie/603");

            // While the catalog cannot answer the page shows the catalog row and says so; nothing is stored and nothing is retried.
            tmdb.IsUnavailable = true;
            var offline = WebUtility.HtmlDecode(await PageAsync(host, "/Details/960"));
            StringAssert.Contains(offline, "Details can't be loaded right now");
            StringAssert.Contains(offline, "Catalog text.");
            Assert.AreEqual(1, Imports());
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"DetailedAt\" FROM \"Works\" WHERE \"Id\" = 960"));

            // The next open takes the details into the same Work and renders them from the database.
            tmdb.IsUnavailable = false;
            var opened = WebUtility.HtmlDecode(await PageAsync(host, "/Details/960"));
            Assert.IsFalse(opened.Contains("Details can't be loaded right now", StringComparison.Ordinal));
            StringAssert.Contains(opened, "Keanu Reeves");
            Assert.AreEqual(2, Imports());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"DetailedAt\" IS NOT NULL AND \"ViewedAt\" IS NOT NULL FROM \"Works\" WHERE \"Id\" = 960"));
            Assert.AreEqual(960L, await host.ScalarAsync("SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'imdb' AND \"Value\" = 'tt0133093'"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"CanonicalTitle\" = 'The Matrix'"));

            // A second open asks no provider; two opens at once of a title without details import it once.
            var asked = tmdb.Requests.Count;
            await PageAsync(host, "/Details/960");
            Assert.AreEqual(asked, tmdb.Requests.Count);
            await host.Fixture.RunSqlAsync("UPDATE \"Works\" SET \"DetailedAt\" = NULL WHERE \"Id\" = 960");
            await Task.WhenAll(PageAsync(host, "/Details/960"), PageAsync(host, "/Details/960"));
            Assert.AreEqual(3, Imports());

            // Details older than a day are shown at once without a provider; the metadata job reads them again.
            await host.Fixture.RunSqlAsync("UPDATE \"Works\" SET \"DetailedAt\" = now() - interval '2 days', \"ViewedAt\" = NULL WHERE \"Id\" = 960");
            asked = tmdb.Requests.Count;
            StringAssert.Contains(await PageAsync(host, "/Details/960"), "Keanu Reeves");
            Assert.AreEqual(asked, tmdb.Requests.Count);
            await FillAsync(host);
            Assert.AreEqual(4, Imports());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"DetailedAt\" > now() - interval '1 hour' FROM \"Works\" WHERE \"Id\" = 960"));
        }

        [TestMethod]
        public async Task Request_OnACatalogTitle_ImportsItsDetailsFirst_AndWhileTheCatalogIsDownTheRequestStaysAndTheJobFillsIt()
        {
            var tmdb = new FakeTmdb();
            await using var host = await CreateHostAsync(fake: tmdb, tvdb: new FakeTvdb());
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear", "CatalogNamespace") VALUES (970, 2, 'The Matrix Show', 2020, 'tmdb_tv'), (971, 1, 'The Matrix', 1999, 'tmdb_movie');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (970, 'tmdb_tv', '1399'), (971, 'tmdb_movie', '603');
                """);

            // The series' request brings its seasons and episodes into the same Work before the request exists.
            using var series = await host.SendAsync(HttpMethod.Post, "/api/v1/works/970/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, series.StatusCode, await series.Content.ReadAsStringAsync());
            var requestId = (await series.Content.ReadFromJsonAsync<CreateRequestResultV1>())!.Id;
            Assert.AreEqual(970L, await host.ScalarAsync($"SELECT \"WorkId\" FROM \"AcquisitionRequests\" WHERE \"Id\" = {requestId}"));
            Assert.AreEqual(24L, await host.ScalarAsync("SELECT count(*) FROM \"WorkUnits\" WHERE \"WorkId\" = 970 AND \"WorkUnitTypeId\" = 2"));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"DetailedAt\" IS NOT NULL FROM \"Works\" WHERE \"Id\" = 970"));

            // While the catalog cannot answer the film's request is still saved as it is; the metadata job brings its details afterwards.
            tmdb.IsUnavailable = true;
            using var film = await host.SendAsync(HttpMethod.Post, "/api/v1/works/971/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, film.StatusCode, await film.Content.ReadAsStringAsync());
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionRequests\" WHERE \"WorkId\" = 971"));
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"DetailedAt\" FROM \"Works\" WHERE \"Id\" = 971"));
            tmdb.IsUnavailable = false;
            await FillAsync(host);
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"DetailedAt\" IS NOT NULL FROM \"Works\" WHERE \"Id\" = 971"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"WorkMetadataFacts\" WHERE \"WorkId\" = 971 AND \"FactKey\" = 'Overview' AND \"Value\" = 'A hacker learns the truth.'"));
        }

        [TestMethod]
        public async Task Request_OnAnAnimeSeasonOfItsOwn_RequestsItsSeries_WhichTakesTheSeasonsWorkIn()
        {
            var aniList = new FakeAniList { FindsFirstSeason = true };
            await using var host = await CreateHostAsync(anime: aniList, tvdb: new FakeTvdb());

            // A season no series held yet is a Work of its own, a favorite of the profile.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "MediaSubtypeId", "CanonicalTitle", "ReleaseYear", "CatalogNamespace") VALUES (800, 2, 1, 'The Matrix Show Season 2', 2021, 'anilist');
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (800, 'anilist', '21');
                INSERT INTO "WatchlistEntries" ("ProfileId", "WorkId") SELECT "ActiveProfileId", 800 FROM "AccountSessions" WHERE "ActiveProfileId" IS NOT NULL ORDER BY "CreatedAt" DESC LIMIT 1;
                """);

            // The request is the series': it comes from its catalog with its episodes, AniList maps its seasons, and the season's Work goes into it.
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/works/800/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var series = (long)(await host.ScalarAsync("SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'tmdb_tv' AND \"Value\" = '1399'"))!;
            Assert.AreEqual(series, await host.ScalarAsync("SELECT \"WorkId\" FROM \"AcquisitionRequests\""));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"Id\" = 800"));
            Assert.AreEqual(2m, await host.ScalarAsync("""SELECT season."Number" FROM "WorkUnitExternalIdentities" AS mapped JOIN "WorkUnits" AS season ON season."Id" = mapped."WorkUnitId" WHERE mapped."Value" = '21'"""));
            Assert.AreEqual(series, await host.ScalarAsync("SELECT \"WorkId\" FROM \"WatchlistEntries\""));
        }

        private static async Task<string> PageAsync(Host host, string path)
        {
            using var page = await host.SendAsync(HttpMethod.Get, path);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            return html;
        }

        private static async Task FillAsync(Host host)
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<FillWorkMetadataV1>().ExecuteAsync<UpdateResult>(new CoverParametersV1(), default);
        }

        private static async Task<Host> CreateHostAsync(bool razor = false, FakeTmdb? fake = null, FakeAniList? anime = null, FakeTvdb? tvdb = null)
        {
            var tmdb = fake ?? new FakeTmdb();
            var aniList = anime ?? new FakeAniList();
            var host = await Host.CreateAsync(razor: razor, library: true, services: services =>
            {
                if (Environment.GetEnvironmentVariable("JULARR_TEST_CACHE_TIMING") == "1")
                {
                    services.Replace(ServiceDescriptor.Singleton(provider => new SqlDatabase(new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(
                        provider.GetRequiredService<IConfiguration>()["JULARR_DB"]!) { Pooling = true }.ConnectionString).Build())));
                }
                services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => tmdb);
                services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => aniList);
                if (tvdb is not null) services.AddHttpClient<TvdbClient>().ConfigurePrimaryHttpMessageHandler(() => tvdb);
            });
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            foreach (var key in tvdb is null ? new[] { "tmdb" } : new[] { "tmdb", "tvdb" })
            {
                var provider = (long)(await host.ScalarAsync($"SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = '{key}'"))!;
                using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret", new SaveProviderSecretDataV1(Token));
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            }
            return host;
        }

        // The Matrix Show on AniList: a first season and a second with its own trailer and cover but no banner. A search finds the second, or the first when asked to.
        private sealed class FakeAniList : HttpMessageHandler
        {
            public int Requests { get; private set; }
            public bool IsUnavailable { get; set; }
            public bool FindsFirstSeason { get; init; }
            private const string First = """{"id":20,"type":"ANIME","format":"TV","title":{"romaji":"The Matrix Show"},"synonyms":[],"startDate":{"year":2020},"episodes":12,"relations":{"edges":[{"relationType":"SEQUEL","node":{"id":21,"type":"ANIME"}}]}}""";
            private const string Second = """{"id":21,"type":"ANIME","format":"TV","title":{"romaji":"The Matrix Show Season 2"},"synonyms":[],"startDate":{"year":2021},"episodes":12,"description":"Season two.","coverImage":{"large":"https://img.anili.st/second.jpg"},"trailer":{"id":"abcdefghijk","site":"youtube"},"relations":{"edges":[{"relationType":"PREQUEL","node":{"id":20,"type":"ANIME"}}]}}""";

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                if (IsUnavailable) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var variables = body.RootElement.GetProperty("variables");
                var found = FindsFirstSeason && variables.TryGetProperty("search", out _) ? First : Second;
                var json = variables.TryGetProperty("id", out var id) ? "{\"data\":{\"Media\":" + (id.GetInt32() == 20 ? First : Second) + "}}" : "{\"data\":{\"Page\":{\"media\":[" + found + "]}}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
        }

        // TheTVDB knows The Matrix Show (81189) with two seasons of twelve episodes.
        private sealed class FakeTvdb : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var episodes = string.Join(',', Enumerable.Range(0, 24).Select(index => $$"""{"id":{{100 + index}},"seasonNumber":{{1 + index / 12}},"number":{{1 + index % 12}},"name":"Episode {{1 + index}}"}"""));
                var json = request.RequestUri!.AbsolutePath switch
                {
                    "/v4/login" => """{"data":{"token":"tvdb-test-token"}}""",
                    "/v4/series/81189/extended" => """{"data":{"id":81189,"name":"The Matrix Show","remoteIds":[]}}""",
                    "/v4/series/81189/episodes/default" => "{\"data\":{\"episodes\":[" + episodes + "]},\"links\":{\"next\":null}}",
                    _ => null
                };
                return Task.FromResult(json is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
        }

        private sealed class FakeTmdb : HttpMessageHandler
        {
            public List<Uri> Requests { get; } = [];
            public bool IsUnavailable { get; set; }
            private const string MovieDetails = """ "genres":[{"id":28,"name":"Action"},{"id":878,"name":"Science Fiction"}],"runtime":136,"production_companies":[{"name":"Village Roadshow"}],"videos":{"results":[{"site":"YouTube","type":"Teaser","key":"aaaaaaaaaaa"},{"site":"YouTube","type":"Trailer","key":"vKQi3bBA1y8","official":true}]},"credits":{"cast":[{"id":6384,"name":"Keanu Reeves","character":"Neo","profile_path":"/neo.jpg"}],"crew":[{"id":9339,"name":"Lana Wachowski","job":"Director"},{"id":1,"name":"Someone","job":"Gaffer"}]} """;
            private const string Movie = """{"id":603,"title":"The Matrix","original_title":"The Matrix","release_date":"1999-03-31","overview":"A hacker learns the truth.","poster_path":"/poster.jpg","backdrop_path":"/backdrop.jpg"}""";
            private const string Show = """{"id":1399,"name":"The Matrix Show","original_name":"The Matrix Show","first_air_date":"2020-01-01","overview":"A show.","poster_path":"/show.jpg"}""";

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri!);
                if (IsUnavailable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                if (request.Headers.Authorization?.Parameter != Token) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                if (QueryHelpers.ParseQuery(request.RequestUri!.Query).GetValueOrDefault("query") == "PagedOnly")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[]}", Encoding.UTF8, "application/json") });
                }
                var body = request.RequestUri!.AbsolutePath switch
                {
                    "/3/trending/movie/week" or "/3/movie/top_rated" or "/3/discover/movie" or "/3/movie/upcoming" or "/3/search/movie" => "{\"results\":[" + Movie + "]}",
                    "/3/trending/tv/week" or "/3/tv/top_rated" or "/3/discover/tv" or "/3/search/tv" => "{\"results\":[" + Show + "]}",
                    "/3/movie/603" => Movie[..^1] + ",\"external_ids\":{\"imdb_id\":\"tt0133093\"},\"belongs_to_collection\":{\"id\":2344,\"name\":\"The Matrix Collection\"}," + MovieDetails
                        + ",\"original_language\":\"en\",\"translations\":{\"translations\":[{\"iso_639_1\":\"de\",\"iso_3166_1\":\"AT\",\"data\":{\"title\":\"Matrix (AT)\",\"overview\":\"\"}},"
                        + "{\"iso_639_1\":\"de\",\"iso_3166_1\":\"DE\",\"data\":{\"title\":\"Matrix\",\"overview\":\"Ein Hacker erfährt die Wahrheit.\"}}]}}",
                    "/3/tv/1399" => Show[..^1] + ",\"backdrop_path\":\"/show-backdrop.jpg\",\"external_ids\":{\"tvdb_id\":81189},\"seasons\":[]}",
                    "/3/movie/603/recommendations" => """{"results":[{"id":604,"title":"The Matrix Reloaded","original_title":"The Matrix Reloaded","release_date":"2003-05-15","poster_path":"/reloaded.jpg"}]}""",
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
