using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Collection.V1;
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
            using var removed = await host.SendAsync(HttpMethod.Delete, Route + "/" + favorites + "/works/10");
            Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode);
            Assert.AreEqual(0, (await WorksAsync(host, favorites)).Length);
            using var page = await host.SendAsync(HttpMethod.Get, "/Collections/" + favorites);
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync());

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

        private static async Task<long> CreateAsync(Host host, CreateCollectionDataV1 data)
        {
            using var created = await host.SendAsync(HttpMethod.Post, Route, data);
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            return (await created.Content.ReadFromJsonAsync<CreateCollectionResultV1>())!.Id;
        }

        private static async Task<long[]> WorksAsync(Host host, long collectionId) =>
            [.. (await ReadAsync<PageResult<WorkCardV1>>(host, Route + "/" + collectionId + "/works")).Items.Select(item => item.Id)];

        private static async Task<T> ReadAsync<T>(Host host, string path)
        {
            using var response = await host.SendAsync(HttpMethod.Get, path);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
    }
}
