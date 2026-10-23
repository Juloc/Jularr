using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.User.Reading.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ReaderSettingsTests
    {
        private const string Route = "/api/v1/reading/settings";

        [TestMethod]
        public async Task Settings_DefaultsSaveAndBounds_BelongToTheProfile()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);

            // Without a choice the defaults apply and no row exists.
            var settings = await ReadAsync(host);
            Assert.AreEqual((ReaderFont.Serif, 20, 165, 100, 68, true), (settings.FontTypeId, settings.Size, settings.LineSpacing, settings.ParagraphSpacing, settings.TextWidth, settings.Hyphenation));
            Assert.IsFalse(settings.IsSaved);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"ProfileReaderSettings\""));

            // A save replaces every choice; saving again keeps one row.
            using var saved = await host.SendAsync(HttpMethod.Put, Route, new SaveReaderSettingsDataV1(ReaderFont.Sans, 26, 180, 60, 55, false));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var again = await host.SendAsync(HttpMethod.Put, Route, new SaveReaderSettingsDataV1(ReaderFont.Sans, 27, 180, 60, 55, false));
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode, await again.Content.ReadAsStringAsync());
            settings = await ReadAsync(host);
            Assert.AreEqual((ReaderFont.Sans, 27, 180, 60, 55, false), (settings.FontTypeId, settings.Size, settings.LineSpacing, settings.ParagraphSpacing, settings.TextWidth, settings.Hyphenation));
            Assert.IsTrue(settings.IsSaved);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"ProfileReaderSettings\""));

            // Values outside the controls' ranges are refused.
            using var large = await host.SendAsync(HttpMethod.Put, Route, new SaveReaderSettingsDataV1(ReaderFont.Serif, 41, 165, 100, 68, true));
            Assert.AreEqual(HttpStatusCode.BadRequest, large.StatusCode);
            using var wide = await host.SendAsync(HttpMethod.Put, Route, new SaveReaderSettingsDataV1(ReaderFont.Serif, 20, 165, 100, 91, true));
            Assert.AreEqual(HttpStatusCode.BadRequest, wide.StatusCode);
            using var spaced = await host.SendAsync(HttpMethod.Put, Route, new SaveReaderSettingsDataV1(ReaderFont.Serif, 20, 165, 201, 68, true));
            Assert.AreEqual(HttpStatusCode.BadRequest, spaced.StatusCode);

            // Another profile of the same account keeps its own row; the services only ever name the active one.
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Profiles" ("Id", "OwnerAccountId", "DisplayName", "UiLocaleId") SELECT 77, "OwnerAccountId", 'Other', "UiLocaleId" FROM "Profiles" LIMIT 1;
                INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") SELECT "OwnerAccountId", 77 FROM "Profiles" WHERE "Id" = 77;
                INSERT INTO "ProfileReaderSettings" ("ProfileId", "Size") VALUES (77, 31);
                """);
            using var third = await host.SendAsync(HttpMethod.Put, Route, new SaveReaderSettingsDataV1(ReaderFont.Serif, 22, 165, 100, 68, true));
            Assert.AreEqual(HttpStatusCode.OK, third.StatusCode, await third.Content.ReadAsStringAsync());
            Assert.AreEqual(31, await host.ScalarAsync("SELECT \"Size\" FROM \"ProfileReaderSettings\" WHERE \"ProfileId\" = 77"));

            // The reading module switches the services off.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Reading'");
            using var off = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
        }

        private static async Task<ReaderSettingsV1> ReadAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, Route);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<ReaderSettingsV1>())!;
        }
    }
}
