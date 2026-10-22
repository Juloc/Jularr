using System.Net;
using System.Net.Http.Json;
using System.Security;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.Public.Setup.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Public.Setup.V1;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class SetupTests
    {
        [TestMethod]
        public async Task Razor_FirstOwner_ValidatesRollsBackAndSignsIn()
        {
            await using var host = await Host.CreateAsync(razor: true, storage: true);
            await host.Fixture.RunSqlAsync("TRUNCATE \"Accounts\" RESTART IDENTITY CASCADE");

            // Fresh instances enter setup.
            using var login = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
            Assert.AreEqual("/Setup", login.Headers.Location!.OriginalString);
            using var setup = await host.SendAsync(HttpMethod.Get, "/Setup");
            var html = await setup.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, setup.StatusCode, html);
            StringAssert.Contains(html, "minlength=\"12\"");
            StringAssert.Contains(html, "name=\"PasswordConfirmation\"");

            // Two names, two password eyes and the steps with the first one current.
            StringAssert.Contains(html, "name=\"ProfileName\"");
            Assert.IsFalse(html.Contains("name=\"DisplayName\"", StringComparison.Ordinal));
            Assert.AreEqual(2, Regex.Matches(html, "data-password-reveal").Count);
            Assert.AreEqual(1, Regex.Matches(html, "stepper__step--current").Count);
            Assert.AreEqual(11, Regex.Matches(html, "stepper__step--upcoming").Count);

            // The owner's language starts as the instance's, however the shipped languages sort.
            var chosen = System.Text.RegularExpressions.Regex.Match(html, @"<option(?=[^>]*\bselected\b)[^>]*value=""(\d+)""").Groups[1].Value;
            Assert.AreEqual(Convert.ToString(await host.ScalarAsync("SELECT \"UiLocaleId\" FROM \"InstanceSettings\""), System.Globalization.CultureInfo.InvariantCulture), chosen, html);
            using var missingToken = await host.SendAsync(HttpMethod.Post, "/Setup", new Dictionary<string, string>());
            Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);

            // Fields and cross-field validation.
            using var shortPassword = await host.SendAsync(HttpMethod.Post, "/Setup", Form(html, "short", "short"));
            Assert.AreEqual(HttpStatusCode.BadRequest, shortPassword.StatusCode);
            using var weakPassword = await host.SendAsync(HttpMethod.Post, "/Setup", Form(html, "ownerpassword123", "ownerpassword123"));
            Assert.AreEqual(HttpStatusCode.BadRequest, weakPassword.StatusCode);
            StringAssert.Contains(await weakPassword.Content.ReadAsStringAsync(), "at least three of");
            using var invalidBinding = await host.SendAsync(HttpMethod.Post, "/Setup", Form(html, "owner-password-123", "owner-password-123", "not-a-number"));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidBinding.StatusCode);
            StringAssert.Contains(await invalidBinding.Content.ReadAsStringAsync(), "name=\"PasswordConfirmation\"");
            using var mismatch = await host.SendAsync(HttpMethod.Post, "/Setup", Form(html, "owner-password-123", "another-password-123"));
            Assert.AreEqual(HttpStatusCode.BadRequest, mismatch.StatusCode);
            StringAssert.Contains(await mismatch.Content.ReadAsStringAsync(), "The passwords do not match.");
            using var unknownLocale = await host.SendAsync(HttpMethod.Post, "/Setup", Form(html, "owner-password-123", "owner-password-123", "999"));
            Assert.AreEqual(HttpStatusCode.BadRequest, unknownLocale.StatusCode);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"Accounts\""));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"InstanceSetupState\" WHERE \"OwnerCreatedAt\" IS NOT NULL"));
            Assert.IsFalse((await unknownLocale.Content.ReadAsStringAsync()).Contains("owner-password-123", StringComparison.Ordinal));

            // Owner, profile and session commit together.
            using var created = await host.SendAsync(HttpMethod.Post, "/Setup", Form(html, "owner-password-123", "owner-password-123"));
            Assert.AreEqual("owner_one", await host.ScalarAsync("SELECT \"Username\" FROM \"Accounts\""));
            Assert.AreEqual(DBNull.Value, await host.ScalarAsync("SELECT \"Email\" FROM \"Accounts\""));
            Assert.AreEqual("First Owner", await host.ScalarAsync("SELECT \"DisplayName\" FROM \"Profiles\""));
            Assert.AreEqual("owner_one", await host.ScalarAsync("SELECT \"DisplayName\" FROM \"Accounts\""));
            Assert.AreEqual(HttpStatusCode.Redirect, created.StatusCode);
            Assert.AreEqual("/Setup/Progress", created.Headers.Location!.OriginalString);

            // The wizard resumes at its first step, where the way to start is chosen.
            using var resume = await host.SendAsync(HttpMethod.Get, "/Setup/Progress");
            Assert.AreEqual("/Setup/Begin", resume.Headers.Location!.OriginalString);
            using var begin = await host.SendAsync(HttpMethod.Get, "/Setup/Begin");
            var beginHtml = await begin.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, begin.StatusCode, beginHtml);
            Assert.AreEqual(2, Regex.Matches(beginHtml, "class=\"setup-choice\"").Count, beginHtml);
            Assert.IsFalse(beginHtml.Contains("/Setup/Backup", StringComparison.Ordinal));
            Assert.AreEqual(0, Regex.Matches(beginHtml, "<h1|<h2").Count);
            Assert.AreEqual(0, Regex.Matches(beginHtml, "app-sidebar|data-shell=\"app\"").Count, "The setup has no application menu.");
            using var started = await host.SendAsync(HttpMethod.Post, "/Setup/Begin?handler=Start", Token(beginHtml, ("start", "Takeover")));
            Assert.AreEqual("/Setup/Progress", started.Headers.Location!.OriginalString);
            Assert.AreEqual((byte)2, Convert.ToByte(await host.ScalarAsync("SELECT \"SetupStartTypeId\" FROM \"InstanceSetupState\"")));
            using var storageResume = await host.SendAsync(HttpMethod.Get, "/Setup/Progress");
            Assert.AreEqual("/Setup/Storage", storageResume.Headers.Location!.OriginalString);

            // Taking over adds its step after the storage; the other start does not.
            using var storage = await host.SendAsync(HttpMethod.Get, "/Setup/Storage");
            var storageHtml = await storage.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, storage.StatusCode, storageHtml);
            StringAssert.Contains(storageHtml, "id=\"storage-add-Smb\"");
            using var takeover = await host.SendAsync(HttpMethod.Post, "/Setup/Progress", Token(storageHtml, ("place", "Storage")));
            Assert.AreEqual("/Setup/Takeover", takeover.Headers.Location!.OriginalString);
            using var takeoverPage = await host.SendAsync(HttpMethod.Get, "/Setup/Takeover");
            Assert.AreEqual(HttpStatusCode.OK, takeoverPage.StatusCode, await takeoverPage.Content.ReadAsStringAsync());
            using var back = await host.SendAsync(HttpMethod.Post, "/Setup/Progress", Token(storageHtml, ("place", "Begin")));
            Assert.AreEqual("/Setup/Storage", back.Headers.Location!.OriginalString);
            using var resumed = await host.SendAsync(HttpMethod.Get, "/Setup/Progress");
            Assert.AreEqual("/Setup/Takeover", resumed.Headers.Location!.OriginalString, "Going back does not take the furthest step away.");

            // Every topic is a step of its own, in order; the editors keep their links inside the wizard.
            var places = new[] { ("Takeover", "/Setup/Instance"), ("Modules", "/Setup/Profiles"), ("Profiles", "/Setup/Libraries"), ("Libraries", "/Setup/Requests"), ("Requests", "/Setup/DownloadStorage"),
                ("DownloadStorage", "/Setup/Downloads"), ("Downloads", "/Setup/Indexers"), ("Indexers", "/Setup/Providers"), ("Providers", "/Setup/Overview") };
            foreach (var (place, next) in places)
            {
                using var step = await host.SendAsync(HttpMethod.Post, "/Setup/Progress", Token(storageHtml, ("place", place)));
                Assert.AreEqual(next, step.Headers.Location!.OriginalString, place + await step.Content.ReadAsStringAsync());
                using var page = await host.SendAsync(HttpMethod.Get, next);
                Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, next + await page.Content.ReadAsStringAsync());
                var pageHtml = await page.Content.ReadAsStringAsync();
                StringAssert.Contains(pageHtml, "setup-wizard");
                Assert.AreEqual(1, Regex.Matches(pageHtml, "stepper__step--current").Count, next);
                Assert.IsFalse(pageHtml.Contains("navigation-tabs", StringComparison.Ordinal), next + " has tabs.");
            }

            using var profiles = await host.SendAsync(HttpMethod.Get, "/Setup/Profiles");
            var profilesHtml = await profiles.Content.ReadAsStringAsync();
            StringAssert.Contains(profilesHtml, "href=\"/Setup/Profiles/New?type=Movie\"");
            using var overview = await host.SendAsync(HttpMethod.Get, "/Setup/Overview");
            var overviewHtml = await overview.Content.ReadAsStringAsync();
            StringAssert.Contains(overviewHtml, "href=\"/Setup/Libraries\"");
            StringAssert.Contains(overviewHtml, "href=\"/Setup/Providers\"");

            // Missing essentials block the end; once nothing blocks, Finish closes the wizard.
            await host.Fixture.RunSqlAsync("UPDATE \"MediaTypes\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Movie'");
            using var blocked = await host.SendAsync(HttpMethod.Get, "/Setup/Overview");
            StringAssert.Contains(await blocked.Content.ReadAsStringAsync(), "Movies and series need a TMDB key.");
            await host.Fixture.RunSqlAsync("UPDATE \"MediaTypes\" SET \"IsEnabled\" = FALSE; UPDATE \"MediaSubtypes\" SET \"IsEnabled\" = FALSE");
            using var ready = await host.SendAsync(HttpMethod.Get, "/Setup/Overview");
            var readyHtml = await ready.Content.ReadAsStringAsync();
            StringAssert.Contains(readyHtml, "has no indexer yet.");
            var version = Regex.Match(readyHtml, "name=\"version\" value=\"(\\d+)\"", RegexOptions.IgnoreCase).Groups[1].Value;
            using var finished = await host.SendAsync(HttpMethod.Post, "/Setup/Overview?handler=Finish", Token(readyHtml, ("version", version), ("acceptMissing", "true")));
            Assert.AreEqual("/", finished.Headers.Location!.OriginalString);
            using var closed = await host.SendAsync(HttpMethod.Get, "/Setup/Progress");
            Assert.AreEqual("/", closed.Headers.Location!.OriginalString);
            using var late = await host.SendAsync(HttpMethod.Post, "/Setup/Progress", Token(readyHtml, ("place", "Storage")));
            Assert.AreEqual(HttpStatusCode.BadRequest, late.StatusCode);
            using var adminStill = await host.SendAsync(HttpMethod.Get, "/Admin/Instance");
            Assert.AreEqual(HttpStatusCode.OK, adminStill.StatusCode);
            using var settings = await host.SendAsync(HttpMethod.Get, "/Settings");
            Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);
            StringAssert.Contains(await settings.Content.ReadAsStringAsync(), "First Owner");
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Accounts\" WHERE \"AccountRoleTypeId\" = 1 AND \"IsEnabled\" AND \"EmailVerifiedAt\" IS NULL"));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountProfiles\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\""));

            // The public API cannot create another Owner.
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/setup");
            StringAssert.Contains(await state.Content.ReadAsStringAsync(), "\"requiresOwner\":false,\"isCompleted\":true");
            using var replay = await host.SendAsync(HttpMethod.Post, "/api/v1/setup/owner",
                new CreateOwnerDataV1("second_owner", "Second", "owner-password-123", "owner-password-123", 1));
            Assert.AreEqual(HttpStatusCode.Forbidden, replay.StatusCode);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Accounts\""));
        }

        [TestMethod]
        public async Task Modules_StaleRow_RollsBackSettingsAndProgressThenSavesTogether()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync("TRUNCATE \"Accounts\" RESTART IDENTITY CASCADE");
            using var auth = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/setup/owner", new CreateOwnerDataV1("owner_one", "Owner", "owner-password-123", "owner-password-123", 1));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            using var page = await host.SendAsync(HttpMethod.Get, "/Setup/Instance");
            var html = await page.Content.ReadAsStringAsync();
            using var read = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/setup/modules");
            var modules = (await read.Content.ReadFromJsonAsync<BaseDataList<SetupModuleV1>>())!;
            var settingsVersion = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"InstanceSettings\""))!;
            var setupVersion = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"InstanceSetupState\""))!;
            var form = Token(html, ("Name", "  My server  "), ("Version", settingsVersion.ToString()), ("SetupVersion", setupVersion.ToString()));
            for (var index = 0; index < modules.Count; index++)
            {
                var module = modules[index];
                form[$"Modules[{index}].Id"] = module.Id.ToString();
                form[$"Modules[{index}].Version"] = module.Version.ToString();
                form[$"Modules[{index}].IsEnabled"] = "True";
            }

            form[$"Modules[{modules.Count - 1}].Version"] = "99999";
            using var stale = await host.SendAsync(HttpMethod.Post, "/Setup/Instance", form);
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode, await stale.Content.ReadAsStringAsync());
            Assert.AreEqual(settingsVersion, await host.ScalarAsync("SELECT \"Version\" FROM \"InstanceSettings\""));
            Assert.AreEqual(setupVersion, await host.ScalarAsync("SELECT \"Version\" FROM \"InstanceSetupState\""));
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'Devices'"));

            form[$"Modules[{modules.Count - 1}].Version"] = modules[^1].Version.ToString();

            // The media types go with the modules: a changed one is saved, an unchanged one is left alone.
            var musicVersion = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"MediaTypes\" WHERE \"Key\" = 'Music'"))!;
            var movieVersion = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"MediaTypes\" WHERE \"Key\" = 'Movie'"))!;
            form["Media[0].Key"] = "Movie";
            form["Media[0].Version"] = movieVersion.ToString();
            form["Media[0].IsEnabled"] = "True";
            form["Media[1].Key"] = "Music";
            form["Media[1].Version"] = musicVersion.ToString();
            form["Media[1].IsEnabled"] = "False";
            using var saved = await host.SendAsync(HttpMethod.Post, "/Setup/Instance", form);
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.AreEqual("/Setup/Progress", saved.Headers.Location!.OriginalString);
            using var afterModules = await host.SendAsync(HttpMethod.Get, "/Setup/Progress");
            Assert.AreEqual("/Setup/Profiles", afterModules.Headers.Location!.OriginalString);
            Assert.AreEqual("My server", await host.ScalarAsync("SELECT \"Name\" FROM \"InstanceSettings\""));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'Devices'"));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'Registration'"));
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"MediaTypes\" WHERE \"Key\" = 'Music'"));
            Assert.AreEqual(movieVersion, await host.ScalarAsync("SELECT \"Version\" FROM \"MediaTypes\" WHERE \"Key\" = 'Movie'"));

            using var nextPage = await host.SendAsync(HttpMethod.Get, "/Setup/Providers");
            var nextHtml = await nextPage.Content.ReadAsStringAsync();
            using var libraries = await host.SendAsync(HttpMethod.Post, "/Setup/Progress", Token(nextHtml, ("place", "Profiles")));
            Assert.AreEqual("/Setup/Libraries", libraries.Headers.Location!.OriginalString, await libraries.Content.ReadAsStringAsync());

            // Leaving the libraries step switches off the media without a folder; the providers step saves keys and switches together.
            using var requests = await host.SendAsync(HttpMethod.Post, "/Setup/Progress", Token(nextHtml, ("place", "Libraries")));
            Assert.AreEqual("/Setup/Requests", requests.Headers.Location!.OriginalString, await requests.Content.ReadAsStringAsync());
            using var providersPage = await host.SendAsync(HttpMethod.Get, "/Setup/Providers");
            var providersHtml = await providersPage.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, providersPage.StatusCode, providersHtml);
            var setupVersion2 = Regex.Match(providersHtml, "name=\"Version\" value=\"(\\d+)\"").Groups[1].Value;
            var tmdbIndex = Regex.Match(providersHtml, "name=\"Providers\\[(\\d+)\\]\\.Key\" value=\"tmdb\"").Groups[1].Value;
            Assert.IsTrue(tmdbIndex.Length > 0, providersHtml);
            var keyed = Token(providersHtml, ("Version", setupVersion2));
            foreach (Match row in Regex.Matches(providersHtml, "name=\"Providers\\[(\\d+)\\]\\.Key\" value=\"([^\"]+)\""))
            {
                keyed["Providers[" + row.Groups[1].Value + "].Key"] = row.Groups[2].Value;
                keyed["Providers[" + row.Groups[1].Value + "].IsEnabled"] = row.Groups[2].Value == "tmdb" ? "True" : "False";
            }

            keyed["Providers[" + tmdbIndex + "].Secret"] = "setup-key";
            using var savedProviders = await host.SendAsync(HttpMethod.Post, "/Setup/Providers", keyed);
            Assert.AreEqual("/Setup/Progress", savedProviders.Headers.Location!.OriginalString, await savedProviders.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"ProtectedSecret\" IS NOT NULL FROM \"Providers\" WHERE \"Key\" = 'tmdb'"));
            using var afterProviders = await host.SendAsync(HttpMethod.Get, "/Setup/Progress");
            Assert.AreEqual("/Setup/Overview", afterProviders.Headers.Location!.OriginalString);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"MediaTypes\" WHERE \"IsEnabled\""));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"MediaSubtypes\" WHERE \"IsEnabled\""));
            using var overviewPage = await host.SendAsync(HttpMethod.Get, "/Setup/Overview");
            var overviewHtml = await overviewPage.Content.ReadAsStringAsync();
            var current = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"InstanceSetupState\""))!;
            using var finish = await host.SendAsync(HttpMethod.Post, "/Setup/Overview?handler=Finish", Token(overviewHtml, ("version", current.ToString()), ("acceptMissing", "true")));
            Assert.AreEqual("/", finish.Headers.Location!.OriginalString, await finish.Content.ReadAsStringAsync());

            // Local account requests remain available without a Plex connection and always await approval.
            using var registered = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/register", new RegisterDataV1("applicant", "applicant-password-123", "applicant-password-123", 1));
            Assert.AreEqual(HttpStatusCode.OK, registered.StatusCode, await registered.Content.ReadAsStringAsync());
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"Accounts\" WHERE \"Username\" = 'applicant'"));
        }

        [TestMethod]
        public async Task FirstOwner_DisabledGeneratedLanguage_EnablesAndSharesItWithTheInstance()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync("TRUNCATE \"Accounts\" RESTART IDENTITY CASCADE");
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "UiLocales" ("Locale", "Name", "IsEnabled") VALUES ('fr', 'French', FALSE);
                INSERT INTO "UiTranslations" ("UiLocaleId", "UiTranslationMessageId", "Text", "UiTranslationStatusTypeId", "MessageVersion")
                SELECT locale."Id", message."Id", 'Configuration', 2, message."Version"
                FROM "UiLocales" AS locale CROSS JOIN "UiTranslationMessages" AS message
                WHERE locale."Locale" = 'fr' AND message."Key" = 'setup.title';
                """);
            using var auth = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var locales = await host.SendAsync(HttpMethod.Get, "/api/v1/setup/locales");
            var choices = (await locales.Content.ReadFromJsonAsync<BaseDataList<SetupLocaleV1>>())!;
            var french = choices.Single(locale => locale.Locale == "fr");
            Assert.IsTrue(french.IsMachineTranslated);
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"UiLocales\" WHERE \"Locale\" = 'fr'"));
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/setup/owner", new CreateOwnerDataV1("owner_one", "Owner", "owner-password-123", "owner-password-123", french.Id));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"UiLocales\" WHERE \"Locale\" = 'fr'"));
            Assert.AreEqual(french.Id, await host.ScalarAsync("SELECT \"UiLocaleId\" FROM \"InstanceSettings\""));
            Assert.AreEqual(french.Id, await host.ScalarAsync("SELECT \"UiLocaleId\" FROM \"Profiles\""));
        }

        [TestMethod]
        public async Task Downloads_TooManyConnections_UsesCanonicalBoundsAndAnInlineError()
        {
            await using var host = await Host.CreateAsync(razor: true);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            using var page = await host.SendAsync(HttpMethod.Get, "/Setup/Downloads?type=Usenet");
            var html = await page.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "name=\"MaxConnections\"");
            Assert.IsTrue(Regex.IsMatch(html, "<input[^>]*name=\"MaxConnections\"[^>]*min=\"1\"[^>]*max=\"50\""), html);
            using var invalid = await host.SendAsync(HttpMethod.Post, "/Setup/Downloads?type=Usenet", Token(html,
                ("AcquisitionConnectionTypeId", "6"), ("Name", "News"), ("Host", "news.example.test"), ("IsEnabled", "true"), ("Priority", "1"), ("MaxConnections", "100")));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            var failed = await invalid.Content.ReadAsStringAsync();
            StringAssert.Contains(failed, "aria-invalid=\"true\"");
            StringAssert.Contains(failed, "The value is outside the allowed range.");
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"AcquisitionConnections\""));
        }

        [TestMethod]
        public async Task Libraries_OnlyAnimeRoot_RetainsItsParentAndCompletesWithTheNewKey()
        {
            await using var host = await Host.CreateAsync(razor: true);
            using var auth = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceSetupState" SET "OwnerCreatedAt" = now();
                INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") VALUES ('Test', 3, '/fixture', FALSE);
                INSERT INTO "LibraryRoots" ("Name", "MediaTypeId", "MediaSubtypeId", "StorageMountId", "RelativePath")
                SELECT 'Anime', type."Id", subtype."Id", mount."Id", 'Anime'
                FROM "MediaTypes" AS type CROSS JOIN "MediaSubtypes" AS subtype CROSS JOIN "StorageMounts" AS mount
                WHERE type."Key" = 'Series' AND subtype."Key" = 'Anime';
                """);
            var version = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"InstanceSetupState\""))!;
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/setup/libraries", new CompleteSetupDataV1(version));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"MediaTypes\" WHERE \"Key\" = 'Series'"));
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"MediaTypes\" WHERE \"Key\" = 'Movie'"));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"MediaSubtypes\" WHERE \"Key\" = 'Anime'"));
            version = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"InstanceSetupState\""))!;
            using var providers = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/setup/providers", new { version, providers = new[] { new { key = "tmdb", isEnabled = true, secret = "test-tmdb-key", clientId = (string?)null } } });
            Assert.AreEqual(HttpStatusCode.OK, providers.StatusCode, await providers.Content.ReadAsStringAsync());
            version = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"InstanceSetupState\""))!;

            // Arr+ is on without an indexer or download client: finishing needs the Owner's word for it.
            using var refused = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/setup/completion", new FinishSetupDataV1(version));
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
            using var completed = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/setup/completion", new FinishSetupDataV1(version, true));
            Assert.AreEqual(HttpStatusCode.OK, completed.StatusCode, await completed.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"CompletedAt\" IS NOT NULL FROM \"InstanceSetupState\""));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"ProtectedSecret\" IS NOT NULL FROM \"Providers\" WHERE \"Key\" = 'tmdb'"));
            using var replay = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/setup/completion", new FinishSetupDataV1(version, true));
            Assert.AreEqual(HttpStatusCode.BadRequest, replay.StatusCode);
        }

        // The Owner's instance as setup leaves it: signed in, owner created, nothing else configured.
        private static async Task<Host> OwnerHostAsync()
        {
            var host = await Host.CreateAsync(razor: true);
            using var auth = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceSetupState\" SET \"OwnerCreatedAt\" = now();");
            return host;
        }

        private static async Task<SetupProgressV1> ProgressAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/setup");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<SetupProgressV1>())!;
        }

        [TestMethod]
        public async Task Checks_EmptyInstance_NamesEverythingTheChosenModulesStillNeed()
        {
            await using var host = await OwnerHostAsync();

            // Straight from the start: Arr+ and the library are on, so storage, an indexer, a client, folders and the TMDB key are missing.
            var empty = await ProgressAsync(host);
            foreach (var key in new[] { "storage", "indexer", "downloader", "tmdb", "library.type.Movie", "library.type.Series" })
            {
                CollectionAssert.Contains(empty.Blockers, key);
            }

            CollectionAssert.DoesNotContain(empty.Blockers, "modules");
            CollectionAssert.Contains(empty.Warnings, "requests.default");

            // Leaving the Libraries step switches the media without a folder off: the missing folder stays visible as the empty library.
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/setup/libraries", new CompleteSetupDataV1(empty.Version));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            var clicked = await ProgressAsync(host);
            CollectionAssert.IsSubsetOf(new[] { "storage", "indexer", "downloader" }, clicked.Blockers);
            CollectionAssert.DoesNotContain(clicked.Blockers, "tmdb");
            CollectionAssert.Contains(clicked.Warnings, "library.none");
            Assert.IsFalse(clicked.Blockers.Any(key => key.StartsWith("library.type.", StringComparison.Ordinal)));

            // No function at all and a takeover that brought nothing.
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = FALSE WHERE "Key" IN ('Library', 'Playback', 'Reading', 'Requests', 'Acquisition');
                UPDATE "InstanceSetupState" SET "SetupStartTypeId" = 2;
                """);
            var nothing = await ProgressAsync(host);
            CollectionAssert.Contains(nothing.Blockers, "modules");
            CollectionAssert.Contains(nothing.Blockers, "takeover");
        }

        [TestMethod]
        public async Task Checks_WithoutArrPlus_NeitherClientNorIndexerNorDownloadStorageIsDemanded()
        {
            await using var host = await OwnerHostAsync();
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Acquisition';");
            var progress = await ProgressAsync(host);
            var all = progress.Blockers.Concat(progress.Warnings).ToArray();
            Assert.IsFalse(all.Any(key => key is "indexer" or "downloader" || key.StartsWith("downloadStorage.", StringComparison.Ordinal)), string.Join(", ", all));
            CollectionAssert.Contains(progress.Blockers, "storage");

            // Requests alone need no storage either.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" IN ('Library', 'Playback', 'Reading');");
            progress = await ProgressAsync(host);
            CollectionAssert.DoesNotContain(progress.Blockers, "storage");
            CollectionAssert.DoesNotContain(progress.Warnings, "library.none");
        }

        [TestMethod]
        public async Task Checks_FullyConfiguredInstance_RaisesNothingAndFinishesWithoutConfirmation()
        {
            await using var host = await OwnerHostAsync();
            await host.Fixture.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = FALSE WHERE "Key" IN ('Acquisition', 'Requests');
                UPDATE "MediaTypes" SET "IsEnabled" = ("Key" = 'Series');
                UPDATE "MediaSubtypes" SET "IsEnabled" = FALSE;
                INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") VALUES ('Test', 3, '/fixture', FALSE);
                INSERT INTO "LibraryRoots" ("Name", "MediaTypeId", "MediaSubtypeId", "StorageMountId", "RelativePath")
                SELECT 'Series', type."Id", NULL, mount."Id", 'Series'
                FROM "MediaTypes" AS type CROSS JOIN "StorageMounts" AS mount
                WHERE type."Key" = 'Series';
                UPDATE "Providers" SET "ProtectedSecret" = '\x01'::bytea WHERE "Key" = 'tmdb';
                """);
            var progress = await ProgressAsync(host);

            // Only the machine's FFmpeg is outside what the database knows.
            Assert.AreEqual(0, progress.Blockers.Length, string.Join(", ", progress.Blockers));
            Assert.IsTrue(progress.Warnings.All(key => key == "ffmpeg"), string.Join(", ", progress.Warnings));
            using var completed = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/setup/completion", new FinishSetupDataV1(progress.Version));
            Assert.AreEqual(HttpStatusCode.OK, completed.StatusCode, await completed.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task Finish_WithRequiredThingsMissing_NeedsTheOwnersConfirmation()
        {
            await using var host = await OwnerHostAsync();
            var progress = await ProgressAsync(host);
            Assert.IsTrue(progress.Blockers.Length > 0);
            using var refused = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/setup/completion", new FinishSetupDataV1(progress.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"CompletedAt\" IS NOT NULL FROM \"InstanceSetupState\""));
            using var accepted = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/setup/completion", new FinishSetupDataV1(progress.Version, true));
            Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode, await accepted.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"CompletedAt\" IS NOT NULL FROM \"InstanceSetupState\""));
        }

        [TestMethod]
        public async Task FirstOwner_ConcurrentCreation_OnlyOneSucceedsAndBootstrapStaysClosed()
        {
            await using var host = await Host.CreateAsync();
            await host.Fixture.RunSqlAsync("TRUNCATE \"Accounts\" RESTART IDENTITY CASCADE");

            // Independent service scopes race.
            var attempts = await Task.WhenAll(CreateOwnerAttemptAsync(host, "first_owner"), CreateOwnerAttemptAsync(host, "second_owner"));
            Assert.AreEqual(1, attempts.Count(created => created));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Accounts\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountPasswords\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"Profiles\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\""));
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"InstanceSetupState\" WHERE \"OwnerCreatedAt\" IS NOT NULL"));

            // Deletion does not reopen public bootstrap.
            await host.Fixture.RunSqlAsync("TRUNCATE \"Accounts\" CASCADE");
            using var scope = host.Services.CreateScope();
            var state = await scope.ServiceProvider.GetRequiredService<ReadSetupV1>().ExecuteAsync<ReadSetupResultV1>();
            Assert.IsFalse(state.RequiresOwner);
            Assert.IsFalse(await CreateOwnerAttemptAsync(host, "replacement_owner"));
        }

        private static async Task<bool> CreateOwnerAttemptAsync(Host host, string email)
        {
            using var scope = host.Services.CreateScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<CreateOwnerV1>().ExecuteAsync<LoginResultV1>(
                    new CreateOwnerDataV1(email, "Owner", "owner-password-123", "owner-password-123", 1));
                return true;
            }
            catch (SecurityException)
            {
                return false;
            }
        }

        private static Dictionary<string, string> Token(string html, params (string Key, string Value)[] values)
        {
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
            Assert.IsTrue(token.Success);
            return values.Append(("__RequestVerificationToken", WebUtility.HtmlDecode(token.Groups[1].Value))).ToDictionary(pair => pair.Item1, pair => pair.Item2);
        }

        private static Dictionary<string, string> Form(string html, string password, string confirmation, string uiLocaleId = "1")
        {
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
            Assert.IsTrue(token.Success);
            return new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
                ["Username"] = "owner_one",
                ["ProfileName"] = "First Owner",
                ["Password"] = password,
                ["PasswordConfirmation"] = confirmation,
                ["UiLocaleId"] = uiLocaleId
            };
        }
    }
}
