using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    // A book is wanted once per language: the Wanted list shows each language as its own entry with its own status.
    [TestClass]
    public sealed class WantedBookEntriesTests
    {
        [TestMethod]
        public async Task Wanted_ABookIsOneEntryPerLanguage_EachWithItsOwnStatus_AndTheSummaryCountsEntries()
        {
            await using var host = await Host.CreateAsync(media: true);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            var mount = await LibraryTests.CreateMountAsync(host);
            var root = await LibraryTests.CreateRootAsync(host, mount, "Books", MediaType.Book);

            // Profiles that want German and Indonesian; 1 has a German file, 2 both, 3 none, and a film.
            await host.Fixture.RunSqlAsync($"""
                UPDATE "StorageMounts" SET "IsReadOnly" = FALSE;
                INSERT INTO "UiLocales" ("Locale", "Name") VALUES ('id', 'Indonesia'), ('de-DE', 'Deutsch') ON CONFLICT ("Locale") DO NOTHING;
                INSERT INTO "Profiles" ("Id", "OwnerAccountId", "DisplayName", "UiLocaleId")
                SELECT 9000 + n, (SELECT "OwnerAccountId" FROM "Profiles" ORDER BY "Id" LIMIT 1), 'P' || n, (SELECT "Id" FROM "UiLocales" WHERE "Locale" = CASE n WHEN 1 THEN 'id' ELSE 'de-DE' END) FROM generate_series(1, 2) AS n;
                INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") SELECT "OwnerAccountId", "Id" FROM "Profiles" WHERE "Id" >= 9000;
                UPDATE "Profiles" SET "UiLocaleId" = (SELECT "Id" FROM "UiLocales" WHERE "Locale" = 'de-DE') WHERE "Id" < 9000;
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (701, 3, 'German only'), (702, 3, 'Both'), (703, 3, 'None'), (704, 1, 'A film');
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (801, 701), (802, 702), (803, 703), (804, 704);
                INSERT INTO "WorkEditions" ("Id", "WorkId") VALUES (701, 701), (702, 702);
                INSERT INTO "WorkVersions" ("Id", "WorkId", "WorkEditionId") VALUES (701, 701, 701), (702, 702, 702), (703, 702, 702);
                INSERT INTO "MediaAssets" ("Id", "WorkId", "WorkVersionId", "MediaAssetTypeId") VALUES (1, 701, 701, 3), (2, 702, 702, 3), (3, 702, 703, 3);
                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "MediaAssetId", "LastSeenAt", "ReleaseLanguages") VALUES
                    ({root}, 'a.epub', 1, now(), 1, now(), ARRAY['de']::varchar[]), ({root}, 'b1.epub', 1, now(), 2, now(), ARRAY['de']::varchar[]), ({root}, 'b2.epub', 1, now(), 3, now(), ARRAY['id']::varchar[]);
                """);

            async Task<JsonElement[]> EntriesAsync(int view)
            {
                using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted?pageSize=48&wantedViewTypeId=" + view);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().Select(item => item.Clone())];
            }

            string Describe(JsonElement item) => item.GetProperty("canonicalTitle").GetString() + ":" + (item.TryGetProperty("bookLanguage", out var language) && language.ValueKind == JsonValueKind.String ? language.GetString() : "-")
                + (item.GetProperty("hasMedia").GetBoolean() ? "+" : "-");

            // Everything: each language of a book is an entry (German is there for the first, Indonesian is missing), the film is one entry.
            Assert.AreEqual("A film:--,Both:de+,Both:id+,German only:de+,German only:id-,None:de-,None:id-", string.Join(',', (await EntriesAsync(0)).Select(Describe).Order(StringComparer.Ordinal)));

            // Missing: the languages no file covers, and the film.
            Assert.AreEqual("A film:--,German only:id-,None:de-,None:id-", string.Join(',', (await EntriesAsync(1)).Select(Describe).Order(StringComparer.Ordinal)));

            // The summary counts entries.
            using var summary = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/wanted/summary");
            var counts = await summary.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(7, counts.GetProperty("totalCount").GetInt32());
            Assert.AreEqual(4, counts.GetProperty("missingCount").GetInt32());
        }
    }
}
