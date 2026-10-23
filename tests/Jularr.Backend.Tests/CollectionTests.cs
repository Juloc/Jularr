using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Collection.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Media.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class CollectionTests
    {
        private const string Route = "/api/v1/collections";

        [TestMethod]
        public async Task Collections_HandPickedAndSmart_ShowOnlyTheProfilesVisibleWorks()
        {
            await using var host = await Host.CreateAsync(razor: true, media: true);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (10, 1, 'Old Film', 1999), (20, 1, 'New Film', 2021), (30, 3, 'A Book', 2021);
                """);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());

            // Hand-picked Works keep the order they were added in; names are unique per profile.
            var favorites = await CreateAsync(host, new CreateCollectionDataV1("Favorites", false));
            foreach (var work in new[] { 30, 10, 30 })
            {
                using var added = await host.SendAsync(HttpMethod.Put, Route + "/" + favorites + "/works/" + work);
                Assert.AreEqual(HttpStatusCode.OK, added.StatusCode, await added.Content.ReadAsStringAsync());
            }

            CollectionAssert.AreEqual(new[] { 30L, 10L }, await WorksAsync(host, favorites));
            using var duplicate = await host.SendAsync(HttpMethod.Post, Route, new CreateCollectionDataV1("Favorites", false));
            Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode);
            var picked = await ReadAsync<CollectionV1>(host, Route + "/" + favorites);
            using var filterOnHandPicked = await host.SendAsync(HttpMethod.Put, Route + "/" + favorites, new UpdateCollectionDataV1("Favorites", picked.Version, MediaType.Movie));
            Assert.AreEqual(HttpStatusCode.BadRequest, filterOnHandPicked.StatusCode);

            // A smart collection is a saved filter over what the profile sees.
            var recentFilms = await CreateAsync(host, new CreateCollectionDataV1("Recent films", true));
            var smart = await ReadAsync<CollectionV1>(host, Route + "/" + recentFilms);
            using var filtered = await host.SendAsync(HttpMethod.Put, Route + "/" + recentFilms, new UpdateCollectionDataV1("Recent films", smart.Version, MediaType.Movie, null, 2000));
            Assert.AreEqual(HttpStatusCode.OK, filtered.StatusCode, await filtered.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(new[] { 20L }, await WorksAsync(host, recentFilms));
            using var addToSmart = await host.SendAsync(HttpMethod.Put, Route + "/" + recentFilms + "/works/10");
            Assert.AreEqual(HttpStatusCode.BadRequest, addToSmart.StatusCode);
            using var taken = await host.SendAsync(HttpMethod.Put, Route + "/" + recentFilms, new UpdateCollectionDataV1("Favorites", smart.Version + 1));
            StringAssert.Contains(await taken.Content.ReadAsStringAsync(), "duplicate_value");
            using var stale = await host.SendAsync(HttpMethod.Put, Route + "/" + recentFilms, new UpdateCollectionDataV1("Films", smart.Version));
            StringAssert.Contains(await stale.Content.ReadAsStringAsync(), "outdated_version");
            using var widened = await host.SendAsync(HttpMethod.Put, Route + "/" + recentFilms, new UpdateCollectionDataV1("Films", smart.Version + 1, MediaType.Movie));
            Assert.AreEqual(HttpStatusCode.OK, widened.StatusCode, await widened.Content.ReadAsStringAsync());
            CollectionAssert.AreEquivalent(new[] { 10L, 20L }, await WorksAsync(host, recentFilms));

            // Hidden Works leave every collection; another profile's collection does not exist for this one.
            await host.Fixture.RunSqlAsync("INSERT INTO \"ProfileMediaRules\" (\"ProfileId\", \"MediaTypeId\", \"IsHidden\") VALUES (101, 3, TRUE)");
            CollectionAssert.AreEqual(new[] { 10L }, await WorksAsync(host, favorites));
            await host.Fixture.RunSqlAsync("DELETE FROM \"CollectionWorks\" WHERE \"WorkId\" = 30");
            using var hidden = await host.SendAsync(HttpMethod.Put, Route + "/" + favorites + "/works/30");
            Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode);
            await host.Fixture.RunSqlAsync("UPDATE \"Collections\" SET \"ProfileId\" = 202 WHERE \"Name\" = 'Films'");
            using var foreign = await host.SendAsync(HttpMethod.Get, Route + "/" + recentFilms);
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
            Assert.AreEqual(1, (await ReadAsync<BaseDataList<CollectionV1>>(host, Route)).Count);

            // Details offers the hand-picked ones; removing works there too.
            using var details = await host.SendAsync(HttpMethod.Get, "/Details/10");
            StringAssert.Contains(await details.Content.ReadAsStringAsync(), "Favorites");
            // The collection page names it in the tab and removes a title; deleting asks first.
            using var page = await host.SendAsync(HttpMethod.Get, "/Collections/" + favorites);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "<title>Favorites · ");
            using var removed = await host.SendAsync(HttpMethod.Post, "/Collections/" + favorites + "?handler=Remove&workId=10",
                new Dictionary<string, string> { ["__RequestVerificationToken"] = LibraryTests.Token(html) });
            Assert.AreEqual(HttpStatusCode.Redirect, removed.StatusCode, await removed.Content.ReadAsStringAsync());
            Assert.AreEqual(0, (await WorksAsync(host, favorites)).Length);
            using var confirm = await host.SendAsync(HttpMethod.Get, "/Collections/" + favorites + "?delete=true");
            StringAssert.Contains(await confirm.Content.ReadAsStringAsync(), "The titles themselves stay.");

            // A title of the library is found by name and added from the collection page.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "WantedItems" ("WorkId") VALUES (20);
                INSERT INTO "WorkTitles" ("WorkId", "Title", "IsManual") VALUES (20, 'New Film', TRUE);
                """);
            using var found = await host.SendAsync(HttpMethod.Get, "/Collections/" + favorites + "?q=New");
            var foundHtml = await found.Content.ReadAsStringAsync();
            StringAssert.Contains(foundHtml, "New Film");
            using var addedByPage = await host.SendAsync(HttpMethod.Post, "/Collections/" + favorites + "?handler=Add&workId=20",
                new Dictionary<string, string> { ["__RequestVerificationToken"] = LibraryTests.Token(foundHtml) });
            Assert.AreEqual(HttpStatusCode.Redirect, addedByPage.StatusCode, await addedByPage.Content.ReadAsStringAsync());
            CollectionAssert.Contains(await WorksAsync(host, favorites), 20L);
            using var again = await host.SendAsync(HttpMethod.Get, "/Collections/" + favorites + "?q=New");
            StringAssert.Contains(await again.Content.ReadAsStringAsync(), "In this collection");

            // A profile keeps at most 100 collections, so lists are always complete.
            await host.Fixture.RunSqlAsync("INSERT INTO \"Collections\" (\"ProfileId\", \"Name\", \"IsSmart\") SELECT 101, 'List ' || n, FALSE FROM generate_series(1, 100) AS n");
            using var tooMany = await host.SendAsync(HttpMethod.Post, Route, new CreateCollectionDataV1("One more", false));
            StringAssert.Contains(await tooMany.Content.ReadAsStringAsync(), "collection_limit");

            // Switched off, nothing is read and the navigation hides it.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Collections'");
            using var off = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
            using var home = await host.SendAsync(HttpMethod.Get, "/");
            Assert.IsFalse((await home.Content.ReadAsStringAsync()).Contains("href=\"/Collections\"", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Favorites_ScopesOrderAndMembership_NarrowTheTitlesAndAssignThem()
        {
            await using var host = await Host.CreateAsync(razor: true, media: true);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (10, 1, 'Old Film', 1999), (20, 2, 'New Series', 2021), (30, 3, 'A Book', 2021);
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") VALUES (10, 'Poster', 'https://img.example.test/10.jpg', TRUE), (30, 'Poster', 'https://img.example.test/30.jpg', TRUE);
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (40, 1, 'Loose Film', 2010);
                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "ProgressPositionTypeId", "PositionMs", "DurationMs", "IsCompleted", "CompletedAt")
                    VALUES (101, 10, 1, 500, 1000, FALSE, NULL), (101, 30, 1, 1000, 1000, TRUE, now());
                """);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            foreach (var work in new[] { 10, 20, 30 })
            {
                using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/watchlist/" + work, new UpdateWatchlistDataV1(true));
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            }

            // Several types at once, a release year and the order narrow the favorites.
            async Task<long[]> FavoritesAsync(string query) => [.. (await ReadAsync<PageResult<LibraryItemV1>>(host, "/api/v1/watchlist" + query)).Items.Select(item => item.Id)];
            CollectionAssert.AreEqual(new[] { 30L, 20L, 10L }, await FavoritesAsync(""));
            CollectionAssert.AreEquivalent(new[] { 10L, 30L }, await FavoritesAsync("?mediaTypeIds=1&mediaTypeIds=3"));
            CollectionAssert.AreEqual(new[] { 10L }, await FavoritesAsync("?releaseYearTo=2000"));
            CollectionAssert.AreEqual(new[] { 30L, 20L }, await FavoritesAsync("?releaseYearFrom=2020&librarySortTypeId=2"));
            CollectionAssert.AreEqual(new[] { 20L, 10L }, await FavoritesAsync("?mediaTypeIds=1&mediaTypeIds=2&librarySortTypeId=3"));

            // Progress per favorite, its filter, its order and the count of all favorites.
            var items = (await ReadAsync<PageResult<LibraryItemV1>>(host, "/api/v1/watchlist")).Items;
            Assert.AreEqual(ProgressStateType.InProgress, items.Single(item => item.Id == 10).ProgressStateId);
            Assert.AreEqual(50, items.Single(item => item.Id == 10).ProgressPercent);
            Assert.AreEqual(ProgressStateType.Finished, items.Single(item => item.Id == 30).ProgressStateId);
            Assert.AreEqual(ProgressStateType.NotStarted, items.Single(item => item.Id == 20).ProgressStateId);
            CollectionAssert.AreEqual(new[] { 10L }, await FavoritesAsync("?progressStateId=2"));
            CollectionAssert.AreEqual(new[] { 30L }, await FavoritesAsync("?progressStateId=3"));
            CollectionAssert.AreEqual(new[] { 20L }, await FavoritesAsync("?progressStateId=1"));
            CollectionAssert.AreEqual(new[] { 30L, 10L, 20L }, await FavoritesAsync("?librarySortTypeId=4"));
            Assert.AreEqual(3, (await ReadAsync<WatchlistCountV1>(host, "/api/v1/watchlist/count")).Total);

            // The covers of the first titles and which collection holds which title come in single reads.
            var next = await CreateAsync(host, new CreateCollectionDataV1("Next", false));
            var shelf = await CreateAsync(host, new CreateCollectionDataV1("Shelf", false));
            foreach (var (collection, work) in new[] { (next, 10), (next, 30), (shelf, 30) })
            {
                using var added = await host.SendAsync(HttpMethod.Put, Route + "/" + collection + "/works/" + work);
                Assert.AreEqual(HttpStatusCode.OK, added.StatusCode, await added.Content.ReadAsStringAsync());
            }

            var collections = await ReadAsync<BaseDataList<CollectionV1>>(host, Route);
            CollectionAssert.AreEqual(new[] { "https://img.example.test/30.jpg", "https://img.example.test/10.jpg" }, collections.Single(item => item.Id == next).PosterUrls);
            CollectionAssert.AreEqual(new[] { "https://img.example.test/30.jpg" }, collections.Single(item => item.Id == shelf).PosterUrls);
            var members = await ReadAsync<BaseDataList<CollectionMemberV1>>(host, Route + "/membership?workIds=10&workIds=20&workIds=30");
            CollectionAssert.AreEquivalent(new[] { (10L, next), (30L, next), (30L, shelf) }, members.Select(member => (member.WorkId, member.CollectionId)).ToArray());
            Assert.AreEqual(0, (await ReadAsync<BaseDataList<CollectionMemberV1>>(host, Route + "/membership?workIds=20")).Count);

            // A title put into a collection is a favorite; a favorite that goes leaves every collection.
            using var loose = await host.SendAsync(HttpMethod.Put, Route + "/" + shelf + "/works/40");
            Assert.AreEqual(HttpStatusCode.OK, loose.StatusCode, await loose.Content.ReadAsStringAsync());
            CollectionAssert.Contains(await FavoritesAsync(""), 40L);
            Assert.AreEqual(4, (await ReadAsync<WatchlistCountV1>(host, "/api/v1/watchlist/count")).Total);
            using var unsaved = await host.SendAsync(HttpMethod.Put, "/api/v1/watchlist/40", new UpdateWatchlistDataV1(false));
            Assert.AreEqual(HttpStatusCode.OK, unsaved.StatusCode, await unsaved.Content.ReadAsStringAsync());
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"CollectionWorks\" WHERE \"WorkId\" = 30 AND \"CollectionId\" = " + shelf));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"CollectionWorks\" WHERE \"WorkId\" = 40"));

            // A collection narrows by type and orders by what was added.
            async Task<long[]> InAsync(long collection, string query) => [.. (await ReadAsync<PageResult<LibraryItemV1>>(host, Route + "/" + collection + "/works" + query)).Items.Select(item => item.Id)];
            CollectionAssert.AreEqual(new[] { 10L, 30L }, await InAsync(next, ""));
            CollectionAssert.AreEqual(new[] { 30L, 10L }, await InAsync(next, "?librarySortTypeId=1"));
            CollectionAssert.AreEqual(new[] { 30L }, await InAsync(next, "?mediaTypeIds=3"));

            // The page shows the chosen collection with the collections of each title; the old address leads to it.
            using var page = await host.SendAsync(HttpMethod.Get, "/Favorites?collection=" + next);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "data-favorites-entry=\"30\"");
            StringAssert.Contains(html, "data-collections=\"" + next + "," + shelf + "\"");
            Assert.IsFalse(html.Contains("New Series", StringComparison.Ordinal));
            using var films = await host.SendAsync(HttpMethod.Get, "/Favorites?type=movies");
            var filmHtml = await films.Content.ReadAsStringAsync();
            StringAssert.Contains(filmHtml, "Old Film");
            Assert.IsFalse(filmHtml.Contains("A Book", StringComparison.Ordinal));

            // Checked collections take the titles, unchecked ones that held all of them let go, and a new name makes a collection.
            var token = LibraryTests.Token(html);
            using var assigned = await host.SendAsync(HttpMethod.Post, "/Favorites?handler=Lists", new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", token), new("workIds", "20"), new("workIds", "30"), new("collectionIds", next.ToString()), new("newName", "Fresh")
            ]));
            Assert.AreEqual(HttpStatusCode.Redirect, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(new[] { 10L, 30L, 20L }, await InAsync(next, ""));

            // Shelf held only one of the two titles, so it keeps it; the new collection holds both.
            CollectionAssert.AreEqual(new[] { 30L }, await InAsync(shelf, ""));
            var fresh = (await ReadAsync<BaseDataList<CollectionV1>>(host, Route)).Single(item => item.Name == "Fresh");
            CollectionAssert.AreEquivalent(new[] { 20L, 30L }, await InAsync(fresh.Id, ""));
            using var letGo = await host.SendAsync(HttpMethod.Post, "/Favorites?handler=Lists", new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("workIds", "30"), new("collectionIds", next.ToString())]));
            Assert.AreEqual(HttpStatusCode.Redirect, letGo.StatusCode);
            Assert.AreEqual(0, (await InAsync(shelf, "")).Length);
            CollectionAssert.AreEqual(new[] { 20L }, await InAsync(fresh.Id, ""));

            // Renaming and deleting keep the titles in the favorites.
            var version = (await ReadAsync<CollectionV1>(host, Route + "/" + next)).Version;
            using var renamed = await host.SendAsync(HttpMethod.Post, "/Favorites?handler=Rename&collectionId=" + next,
                new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("Name", "Soon"), new("Version", version.ToString())]));
            Assert.AreEqual(HttpStatusCode.Redirect, renamed.StatusCode, await renamed.Content.ReadAsStringAsync());
            Assert.AreEqual("Soon", (await ReadAsync<CollectionV1>(host, Route + "/" + next)).Name);
            using var deleted = await host.SendAsync(HttpMethod.Post, "/Favorites?handler=DeleteCollection&collectionId=" + next, new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
            Assert.AreEqual(HttpStatusCode.Redirect, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
            Assert.AreEqual(3, (await FavoritesAsync("")).Length);
            using var old = await host.SendAsync(HttpMethod.Get, "/Collections");
            Assert.AreEqual("/Favorites", old.Headers.Location?.OriginalString);
        }

        private static async Task<long> CreateAsync(Host host, CreateCollectionDataV1 data)
        {
            using var created = await host.SendAsync(HttpMethod.Post, Route, data);
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            return (await created.Content.ReadFromJsonAsync<CreateCollectionResultV1>())!.Id;
        }

        private static async Task<long[]> WorksAsync(Host host, long collectionId) =>
            [.. (await ReadAsync<PageResult<LibraryItemV1>>(host, Route + "/" + collectionId + "/works")).Items.Select(item => item.Id)];

        private static async Task<T> ReadAsync<T>(Host host, string path)
        {
            using var response = await host.SendAsync(HttpMethod.Get, path);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
    }
}
