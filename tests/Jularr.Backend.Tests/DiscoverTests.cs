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
using Jularr.Integrations.Logic.AniList;
using Jularr.Integrations.Logic.Tmdb;
using Jularr.Integrations.Data.Tmdb;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
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
        public async Task Catalog_KeepsWhatDiscoverLoaded_AndAnswersTrendingWhileFreshOrTheProviderIsDown()
        {
            var tmdb = new FakeTmdb();
            await using var host = await CreateHostAsync(fake: tmdb);

            // A trending list is kept in its order; searches keep their titles too.
            using var feed = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Trending));
            Assert.AreEqual(HttpStatusCode.OK, feed.StatusCode, await feed.Content.ReadAsStringAsync());
            Assert.AreEqual(1, await host.ScalarAsync("SELECT \"TrendingRank\" FROM \"ExternalWorks\" WHERE \"Namespace\" = 'tmdb_movie' AND \"Value\" = '603'"));
            Assert.AreEqual("https://image.tmdb.org/t/p/w1280/backdrop.jpg", await host.ScalarAsync("SELECT \"BackdropUrl\" FROM \"ExternalWorks\" WHERE \"Value\" = '603'"));
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/search", new SearchDiscoverDataV1("Matrix"));
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode);
            Assert.AreEqual("The Matrix Show", await host.ScalarAsync("SELECT \"Title\" FROM \"ExternalWorks\" WHERE \"Namespace\" = 'tmdb_tv' AND \"Value\" = '1399'"));
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"TrendingRank\" FROM \"ExternalWorks\" WHERE \"Value\" = '1399'"));

            // While the list is fresh the catalog answers, also for a catalog of its own (AniList).
            await host.Fixture.RunSqlAsync("UPDATE \"ExternalWorks\" SET \"Title\" = 'The Matrix (kept)' WHERE \"Value\" = '603'");
            using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Trending));
            Assert.AreEqual("The Matrix (kept)", (await again.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Single().Title);
            using var anime = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Series, DiscoverFeedType.Trending, MediaSubtype.Anime));
            Assert.AreEqual(HttpStatusCode.OK, anime.StatusCode, await anime.Content.ReadAsStringAsync());
            Assert.AreEqual(1, await host.ScalarAsync("SELECT \"TrendingRank\" FROM \"ExternalWorks\" WHERE \"Namespace\" = 'anilist' AND \"Value\" = '21' AND \"MediaSubtypeId\" IS NOT NULL"));
            await host.Fixture.RunSqlAsync("UPDATE \"ExternalWorks\" SET \"Title\" = 'Season 2 (kept)' WHERE \"Namespace\" = 'anilist'");
            using var animeAgain = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Series, DiscoverFeedType.Trending, MediaSubtype.Anime));
            Assert.AreEqual("Season 2 (kept)", (await animeAgain.Content.ReadFromJsonAsync<BaseDataList<DiscoverCatalogItemV1>>())!.Single().Title);

            // An old list is loaded again, and while TMDB cannot answer the last one is shown.
            await host.Fixture.RunSqlAsync("UPDATE \"ExternalWorks\" SET \"FetchedAt\" = now() - interval '7 hours', \"TrendingRank\" = 1 WHERE \"Namespace\" = 'tmdb_tv'");
            tmdb.IsUnavailable = true;
            using var stale = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Series, DiscoverFeedType.Trending));
            Assert.AreEqual(HttpStatusCode.OK, stale.StatusCode, await stale.Content.ReadAsStringAsync());
            Assert.AreEqual("The Matrix Show", (await stale.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Single().Title);
            Assert.IsTrue(tmdb.Requests.Any(request => request.AbsolutePath == "/3/trending/tv/week"));
        }

        [TestMethod]
        public async Task Catalog_WithoutTheDiscoverCacheModule_KeepsNothingAndAsksTheProviders()
        {
            await using var host = await CreateHostAsync();
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'DiscoverCache'");
            using var feed = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Trending));
            Assert.AreEqual("The Matrix", (await feed.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Single().Title);
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/search", new SearchDiscoverDataV1("Matrix"));
            using var anime = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/feed", new ReadDiscoverCatalogFeedDataV1(MediaType.Series, DiscoverFeedType.Trending, MediaSubtype.Anime));
            Assert.AreEqual(HttpStatusCode.OK, anime.StatusCode, await anime.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ExternalWorks\""));
        }

        [TestMethod]
        public async Task Preview_AnswersFromTheLocalCatalog_AndTheTmdbItemNamesItsTrailer()
        {
            await using var host = await CreateHostAsync(fake: new FakeTmdb());
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "ExternalWorks" ("Namespace", "Value", "MediaTypeId", "Title", "ReleaseYear", "Overview", "ImageUrl", "BackdropUrl", "IsAdult") VALUES
                    ('openlibrary', 'OL1W', 3, 'A Book', 1965, 'A long description of the book.', 'https://img.test/book.jpg', NULL, FALSE),
                    ('openlibrary', 'OL2W', 3, 'Adult Book', NULL, 'Not for the preview.', NULL, NULL, TRUE);
                """);

            // No provider is asked: the description is what the catalog keeps; unknown and adult titles have none.
            using var local = await host.SendAsync(HttpMethod.Get, "/api/v1/discover/preview?namespace=openlibrary&externalId=OL1W");
            Assert.AreEqual(HttpStatusCode.OK, local.StatusCode, await local.Content.ReadAsStringAsync());
            var preview = (await local.Content.ReadFromJsonAsync<DiscoverPreviewV1>())!;
            Assert.AreEqual("A Book", preview.Title);
            Assert.AreEqual(1965, preview.ReleaseYear);
            Assert.AreEqual("A long description of the book.", preview.Overview);
            Assert.AreEqual("https://img.test/book.jpg", preview.PosterUrl);
            using var unknown = await host.SendAsync(HttpMethod.Get, "/api/v1/discover/preview?namespace=openlibrary&externalId=missing");
            Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
            using var adult = await host.SendAsync(HttpMethod.Get, "/api/v1/discover/preview?namespace=openlibrary&externalId=OL2W");
            Assert.AreEqual(HttpStatusCode.NotFound, adult.StatusCode);

            // A single TMDB title names its trailer; the lists do not carry one.
            using var item = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/Movie/603");
            Assert.AreEqual("vKQi3bBA1y8", (await item.Content.ReadFromJsonAsync<DiscoverItemV1>())!.TrailerKey);
            using var feed = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Trending));
            Assert.IsNull((await feed.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Single().TrailerKey);
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
        public async Task Catalog_ReadWhole_AnswersEveryShelfAndSuggestsWhileTyping()
        {
            var tmdb = new FakeTmdb();
            await using var host = await CreateHostAsync(fake: tmdb);
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" IN ('tmdb_movie', 'anilist');
                INSERT INTO "ExternalWorks" ("Namespace", "Value", "MediaTypeId", "MediaSubtypeId", "Title", "ReleasedOn", "ImageUrl", "ImdbId", "Popularity", "Rating", "Votes", "TrendingRank", "IsAdult") VALUES
                    ('tmdb_movie', '603', 1, NULL, 'The Matrix', '1999-03-31', 'https://img.test/603.jpg', 'tt0133093', 80, 8.7, 25000, 1, FALSE),
                    ('tmdb_movie', '604', 1, NULL, 'The Matrix Reloaded', '2003-05-15', 'https://img.test/604.jpg', 'tt0234215', 60, 7.0, 10000, NULL, FALSE),
                    ('tmdb_movie', '700', 1, NULL, 'Rated By Five', '2020-01-01', 'https://img.test/700.jpg', NULL, 1, 9.9, 5, NULL, FALSE),
                    ('tmdb_movie', '701', 1, NULL, 'Out Now', current_date - 10, 'https://img.test/701.jpg', NULL, 50, NULL, NULL, NULL, FALSE),
                    ('tmdb_movie', '702', 1, NULL, 'Out Soon', current_date + 30, 'https://img.test/702.jpg', NULL, 40, NULL, NULL, NULL, FALSE),
                    ('tmdb_movie', '703', 1, NULL, 'Matrix Adult', '2001-01-01', 'https://img.test/703.jpg', NULL, 90, 9.0, 9000, 2, TRUE),
                    ('tmdb_movie', '704', 1, NULL, 'Matrix Unread', NULL, NULL, NULL, 99, NULL, NULL, 3, FALSE),
                    ('anilist', '21', 2, 1, 'The Matrix Anime', '2021-04-01', 'https://img.test/21.jpg', NULL, 5000, 8.0, 5000, 1, FALSE);
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
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/Movie/603/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            using var top = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Top));
            var topItems = (await top.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.ToArray();
            Assert.AreEqual(MediaStateType.Requested, topItems[0].MediaStateTypeId);
            Assert.AreEqual(8.7m, topItems[0].Rating);
            Assert.AreEqual(7.0m, topItems[1].Rating);
            Assert.IsNotNull(topItems[0].WorkId);
            using var nextPage = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/feed", new ReadDiscoverFeedDataV1(MediaType.Movie, DiscoverFeedType.Top, 2));
            Assert.AreEqual(0, (await nextPage.Content.ReadFromJsonAsync<BaseDataList<DiscoverItemV1>>())!.Count);

            // Typed words suggest the library's title first, then the others; adult and unread titles stay out.
            using var suggested = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/suggestions", new SuggestDiscoverDataV1("matr"));
            Assert.AreEqual(HttpStatusCode.OK, suggested.StatusCode, await suggested.Content.ReadAsStringAsync());
            var suggestions = (await suggested.Content.ReadFromJsonAsync<BaseDataList<DiscoverSuggestionV1>>())!.ToArray();
            Assert.AreEqual("The Matrix|The Matrix Anime|The Matrix Reloaded", string.Join("|", suggestions.Select(item => item.Title)));
            Assert.AreEqual("movies", suggestions[0].ScopeSlug);
            Assert.IsNotNull(suggestions[0].WorkId);
            Assert.AreEqual("anime", suggestions[1].ScopeSlug);
            using var narrowed = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/suggestions", new SuggestDiscoverDataV1("matrix rel", "movies"));
            Assert.AreEqual("The Matrix Reloaded", (await narrowed.Content.ReadFromJsonAsync<BaseDataList<DiscoverSuggestionV1>>())!.Single().Title);

            // Without full local discovery the shelves come from the providers again; without DiscoverCache nothing is suggested.
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
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/Movie/603/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;
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
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/Movie/603/requests", new CreateRequestDataV1(1));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var workId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Works\""))!;

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

            // Similar titles from TMDB lead to their request.
            StringAssert.Contains(html, "The Matrix Reloaded");
            StringAssert.Contains(html, "/Discover/movies/604");
        }

        [TestMethod]
        public async Task Anime_AniListSeason_IsShownAsItselfAndRequestedAsItsTmdbSeries()
        {
            await using var host = await CreateHostAsync(razor: true);

            // The backend names the scopes of the media-type bar, anime among the catalogs.
            using var scopes = await host.SendAsync(HttpMethod.Get, "/api/v1/discover/scopes");
            var anime = (await scopes.Content.ReadFromJsonAsync<BaseDataList<DiscoverScopeV1>>())!.Single(scope => scope.Slug == "anime");
            Assert.AreEqual((MediaType.Series, (MediaSubtype?)MediaSubtype.Anime, true), (anime.MediaTypeId, anime.MediaSubtypeId, anime.IsCatalog));
            CollectionAssert.AreEqual(new[] { DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New }, anime.Feeds);

            // The anime tab ranks AniList seasons.
            using var tab = await host.SendAsync(HttpMethod.Get, "/?type=anime&handler=Recommendations");
            var html = await tab.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, tab.StatusCode, html);
            StringAssert.Contains(html, "The Matrix Show Season 2");
            StringAssert.Contains(html, "/Discover/anime/anilist/21");

            // The season keeps its own trailer and cover; the missing banner comes from the TMDB series of its first season.
            using var read = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/entry", new ReadDiscoverCatalogEntryDataV1(MediaType.Series, "anilist", "21", MediaSubtype.Anime));
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            var season = (await read.Content.ReadFromJsonAsync<DiscoverCatalogEntryV1>())!;
            Assert.AreEqual("abcdefghijk", season.TrailerKey);
            Assert.AreEqual("https://img.anili.st/second.jpg", season.PosterUrl);
            Assert.AreEqual("https://image.tmdb.org/t/p/w1280/show-backdrop.jpg", season.BackdropUrl);
            Assert.IsNull(season.WorkId);
            using var page = await host.SendAsync(HttpMethod.Get, "/Discover/anime/anilist/21");
            StringAssert.Contains(await page.Content.ReadAsStringAsync(), "https://www.youtube.com/watch?v=abcdefghijk");

            // The request creates the TMDB series as Anime; the season then leads to it.
            using var requested = await host.SendAsync(HttpMethod.Post, "/api/v1/discover/catalog/requests", new RequestDiscoverCatalogDataV1(MediaType.Series, "anilist", "21", 1, MediaSubtype.Anime));
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, await requested.Content.ReadAsStringAsync());
            var workId = (long)(await host.ScalarAsync("SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'tmdb_tv' AND \"Value\" = '1399'"))!;
            Assert.AreEqual((short)1, await host.ScalarAsync("SELECT \"MediaSubtypeId\" FROM \"Works\" WHERE \"Id\" = " + workId));
            using var known = await host.SendAsync(HttpMethod.Get, "/Discover/anime/anilist/21");
            Assert.AreEqual(HttpStatusCode.Redirect, known.StatusCode);
            Assert.AreEqual("/Details/" + workId, known.Headers.Location!.OriginalString);
        }

        [TestMethod]
        public async Task RelatedWork_RequestedFromItsRow_IsTheDiscoverRequestWithoutLeavingThePage()
        {
            await using var host = await CreateHostAsync(razor: true);

            // A Work names a season of an anime that is no Work yet.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (700, 1, 'Source Film', 2001);
                INSERT INTO "ExternalWorks" ("Id", "Namespace", "Value", "MediaTypeId", "MediaSubtypeId", "Title", "ReleaseYear") VALUES (800, 'anilist', '21', 2, 1, 'The Matrix Show Season 2', 2021);
                INSERT INTO "WorkRelations" ("WorkId", "ExternalWorkId", "WorkRelationTypeId", "SourceKey") VALUES (700, 800, 1, 'anilist');
                """);
            using var browser = ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();
            browser.DefaultRequestHeaders.Add("Cookie", string.Join("; ", host.Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
            browser.DefaultRequestHeaders.Add("HX-Request", "true");

            // The row offers the request itself, not a link to Discover.
            using var shown = await browser.GetAsync("/Details/700?tab=related");
            var html = await shown.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, shown.StatusCode, html);
            StringAssert.Contains(html, "handler=RequestRelated");
            StringAssert.Contains(html, "name=\"externalId\" value=\"21\"");
            var token = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
            var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["namespace"] = "anilist", ["externalId"] = "21" };

            // A title this Work does not name is not requested through it.
            using var foreign = await browser.PostAsync("/Details/700?handler=RequestRelated&tab=related", new FormUrlEncodedContent(new Dictionary<string, string>(form) { ["externalId"] = "999" }));
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionRequests\""));

            // The request is made as Discover makes it; the answer is the page again with the hint and no redirect, and the row says Requested instead of offering the button.
            using var requested = await browser.PostAsync("/Details/700?handler=RequestRelated&tab=related", new FormUrlEncodedContent(form));
            var answer = await requested.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, requested.StatusCode, answer);
            StringAssert.Contains(string.Join(' ', requested.Headers.GetValues("HX-Trigger")), "Requested");
            var workId = (long)(await host.ScalarAsync("SELECT \"WorkId\" FROM \"WorkExternalIdentities\" WHERE \"Namespace\" = 'tmdb_tv' AND \"Value\" = '1399'"))!;
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionRequests\" WHERE \"WorkId\" = " + workId));
            StringAssert.Contains(answer, "state-chip__label\">Requested<");
            Assert.IsFalse(answer.Contains("name=\"externalId\" value=\"21\"", StringComparison.Ordinal));

            // Without Requests there is no button and no way to post one.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Requests';");
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "ExternalWorks" ("Id", "Namespace", "Value", "MediaTypeId", "MediaSubtypeId", "Title", "ReleaseYear") VALUES (801, 'anilist', '22', 2, 1, 'Another Season', 2022);
                INSERT INTO "WorkRelations" ("WorkId", "ExternalWorkId", "WorkRelationTypeId", "SourceKey") VALUES (700, 801, 2, 'anilist');
                """);
            using var disabled = await browser.GetAsync("/Details/700?tab=related");
            Assert.IsFalse((await disabled.Content.ReadAsStringAsync()).Contains("handler=RequestRelated", StringComparison.Ordinal));
            using var refused = await browser.PostAsync("/Details/700?handler=RequestRelated&tab=related", new FormUrlEncodedContent(new Dictionary<string, string>(form) { ["externalId"] = "22" }));
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
            StringAssert.Contains(html, "/Discover/movies/603");

            using var browse = await host.SendAsync(HttpMethod.Get, "/?type=series&view=Top");
            var browseHtml = await browse.Content.ReadAsStringAsync();
            StringAssert.Contains(browseHtml, "The Matrix Show");

            // A ranking names itself and leads back to its type; the page keeps the one capsule bar and no row of tabs.
            StringAssert.Contains(browseHtml, "<h2>Top rated</h2>");
            StringAssert.Contains(browseHtml, "href=\"/?type=series\"");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(browseHtml, "class=\"capsule-bar__areas\"").Count);
            Assert.AreEqual(0, System.Text.RegularExpressions.Regex.Matches(browseHtml, "class=\"navigation-tabs[ \"]").Count);

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
            StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "action-dock__paging");
            StringAssert.Contains(await empty.Content.ReadAsStringAsync(), "Page 2");
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

            // A card names where its delayed hover preview comes from, and nothing is requested before the hover.
            using var recommendations = await host.SendAsync(HttpMethod.Get, "/?handler=Recommendations");
            var html = await recommendations.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "data-preview-src=\"/MediaPreview?type=Movie&amp;tmdb=603\"");
            Assert.IsFalse(html.Contains("youtube", StringComparison.OrdinalIgnoreCase));

            // The fragment brings the description and the trailer the provider names; a malformed key is never embedded.
            using var preview = await host.SendAsync(HttpMethod.Get, "/MediaPreview?type=Movie&tmdb=603");
            html = await preview.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode, html);
            StringAssert.Contains(html, "class=\"media-card__preview\"");
            StringAssert.Contains(html, "data-preview-mode=\"trailer\"");
            StringAssert.Contains(html, "data-trailer-id=\"vKQi3bBA1y8\"");
            StringAssert.Contains(html, "data-trailer-action=\"toggle\"");
            StringAssert.Contains(html, "data-trailer-action=\"mute\"");
            StringAssert.Contains(html, "A hacker learns the truth.");
            StringAssert.Contains(html, "href=\"/Discover/movies/603\"");

            // While the provider cannot answer the catalog's own description remains; a title nobody knows needs none.
            tmdb.IsUnavailable = true;
            using var offline = await host.SendAsync(HttpMethod.Get, "/MediaPreview?type=Movie&tmdb=603");
            Assert.AreEqual(HttpStatusCode.OK, offline.StatusCode);
            StringAssert.Contains(await offline.Content.ReadAsStringAsync(), "A hacker learns the truth.");
            using var unknown = await host.SendAsync(HttpMethod.Get, "/MediaPreview?type=Movie&tmdb=999999");
            Assert.AreEqual(HttpStatusCode.NoContent, unknown.StatusCode);
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

        private static async Task<Host> CreateHostAsync(bool razor = false, FakeTmdb? fake = null)
        {
            var tmdb = fake ?? new FakeTmdb();
            var aniList = new FakeAniList();
            var host = await Host.CreateAsync(razor: razor, library: true, services: services =>
            {
                services.AddHttpClient<TmdbClient>().ConfigurePrimaryHttpMessageHandler(() => tmdb);
                services.AddHttpClient<AniListClient>().ConfigurePrimaryHttpMessageHandler(() => aniList);
            });
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            var provider = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Providers\" WHERE \"Key\" = 'tmdb'"))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/providers/" + provider + "/secret", new SaveProviderSecretDataV1(Token));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            return host;
        }

        // The Matrix Show on AniList: a first season and a second with its own trailer and cover but no banner.
        private sealed class FakeAniList : HttpMessageHandler
        {
            private const string First = """{"id":20,"type":"ANIME","format":"TV","title":{"romaji":"The Matrix Show"},"synonyms":[],"startDate":{"year":2020},"episodes":12,"relations":{"edges":[{"relationType":"SEQUEL","node":{"id":21,"type":"ANIME"}}]}}""";
            private const string Second = """{"id":21,"type":"ANIME","format":"TV","title":{"romaji":"The Matrix Show Season 2"},"synonyms":[],"startDate":{"year":2021},"episodes":12,"description":"Season two.","coverImage":{"large":"https://img.anili.st/second.jpg"},"trailer":{"id":"abcdefghijk","site":"youtube"},"relations":{"edges":[{"relationType":"PREQUEL","node":{"id":20,"type":"ANIME"}}]}}""";

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var variables = body.RootElement.GetProperty("variables");
                var json = variables.TryGetProperty("id", out var id) ? "{\"data\":{\"Media\":" + (id.GetInt32() == 20 ? First : Second) + "}}" : "{\"data\":{\"Page\":{\"media\":[" + Second + "]}}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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
