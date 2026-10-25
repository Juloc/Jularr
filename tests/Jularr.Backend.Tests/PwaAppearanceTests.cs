using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class PwaAppearanceTests
    {
        [TestMethod]
        public async Task MainProfile_IsAccountScopedAndAccentNeverFollowsAnUnrelatedProfile()
        {
            await using var host = await Host.CreateAsync(razor: true);
            // The fixture inserts its accounts directly after database migration; select what production migration sets for existing accounts.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"PwaMainProfileId\" = 101 WHERE \"Id\" = 41");
            using var authState = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);

            using var initial = await host.SendAsync(HttpMethod.Get, "/api/v1/profile/pwa");
            Assert.AreEqual(HttpStatusCode.OK, initial.StatusCode);
            var original = (await initial.Content.ReadFromJsonAsync<PwaAppearanceV1>())!;
            Assert.AreEqual(101L, original.MainProfileId);
            Assert.AreEqual("#8040f7", original.AccentHex);

            // The account cannot point its app icon at a foreign profile.
            using var foreign = await host.SendAsync(HttpMethod.Put, "/api/v1/profile/pwa/main/202");
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);

            // A profile that is actually linked can be chosen, but cannot be recolored through this account's active profile.
            await host.Fixture.RunSqlAsync("INSERT INTO \"AccountProfiles\" (\"AccountId\", \"ProfileId\") VALUES (41, 202)");
            using var selected = await host.SendAsync(HttpMethod.Put, "/api/v1/profile/pwa/main/202");
            Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode, await selected.Content.ReadAsStringAsync());
            using var chosen = await host.SendAsync(HttpMethod.Get, "/api/v1/profile/pwa");
            Assert.AreEqual(202L, (await chosen.Content.ReadFromJsonAsync<PwaAppearanceV1>())!.MainProfileId);

            using var deniedAccent = await host.SendAsync(HttpMethod.Put, "/api/v1/profiles/202/pwa/accent", new SavePwaAccentDataV1("#bbeeaa"));
            Assert.IsFalse(deniedAccent.IsSuccessStatusCode);

            // The owner's own color is independent of main-profile selection and of the device-local theme.
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/profiles/101/pwa/accent", new SavePwaAccentDataV1("#bbeeaa"));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var stillShared = await host.SendAsync(HttpMethod.Get, "/api/v1/profile/pwa");
            Assert.AreEqual("#8040f7", (await stillShared.Content.ReadFromJsonAsync<PwaAppearanceV1>())!.AccentHex);
            using var ownAgain = await host.SendAsync(HttpMethod.Put, "/api/v1/profile/pwa/main/101");
            Assert.AreEqual(HttpStatusCode.OK, ownAgain.StatusCode);
            using var recolored = await host.SendAsync(HttpMethod.Get, "/api/v1/profile/pwa");
            Assert.AreEqual("#bbeeaa", (await recolored.Content.ReadFromJsonAsync<PwaAppearanceV1>())!.AccentHex);

            using var invalid = await host.SendAsync(HttpMethod.Put, "/api/v1/profiles/101/pwa/accent", new SavePwaAccentDataV1("#anything"));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);

            using var settings = await host.SendAsync(HttpMethod.Get, "/Settings");
            Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);
            StringAssert.Contains(await settings.Content.ReadAsStringAsync(), "#bbeeaa");
        }

        [TestMethod]
        public async Task RemovingAssignedMainProfileClearsTheSelection()
        {
            await using var host = await Host.CreateAsync();
            using var authState = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (41, 202);
                UPDATE "Accounts" SET "PwaMainProfileId" = 202 WHERE "Id" = 41;
                DELETE FROM "AccountProfiles" WHERE "AccountId" = 41 AND "ProfileId" = 202;
                """);
            using var appearance = await host.SendAsync(HttpMethod.Get, "/api/v1/profile/pwa");
            Assert.AreEqual(HttpStatusCode.OK, appearance.StatusCode);
            var selected = (await appearance.Content.ReadFromJsonAsync<PwaAppearanceV1>())!;
            Assert.IsNull(selected.MainProfileId);
            Assert.AreEqual("#8040f7", selected.AccentHex);
        }
    }
}
