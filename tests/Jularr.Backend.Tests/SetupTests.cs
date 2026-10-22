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
            Assert.AreEqual(4, Regex.Matches(html, "stepper__step--upcoming").Count);

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

            // The wizard resumes at its step and moves on through the real editors.
            using var resume = await host.SendAsync(HttpMethod.Get, "/Setup/Progress");
            Assert.AreEqual("/Setup/Instance", resume.Headers.Location!.OriginalString);
            using var instance = await host.SendAsync(HttpMethod.Get, "/Setup/Instance");
            var instanceHtml = await instance.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, instance.StatusCode, instanceHtml);
            StringAssert.Contains(instanceHtml, "name=\"Modules[0].IsEnabled\"");
            Assert.IsFalse(instanceHtml.Contains("UsersConfirmSources", StringComparison.Ordinal));
            using var next = await host.SendAsync(HttpMethod.Post, "/Setup/Progress", Token(instanceHtml, ("SetupStepTypeId", "2")));
            Assert.AreEqual("/Setup/Storage", next.Headers.Location!.OriginalString);
            using var back = await host.SendAsync(HttpMethod.Post, "/Setup/Progress", Token(instanceHtml, ("SetupStepTypeId", "1")));
            Assert.AreEqual(HttpStatusCode.Redirect, back.StatusCode);
            using var resumed = await host.SendAsync(HttpMethod.Get, "/Setup/Progress");
            Assert.AreEqual("/Setup/Storage", resumed.Headers.Location!.OriginalString);
            using var storage = await host.SendAsync(HttpMethod.Get, "/Setup/Storage");
            Assert.AreEqual(HttpStatusCode.OK, storage.StatusCode, await storage.Content.ReadAsStringAsync());
            var storageHtml = await storage.Content.ReadAsStringAsync();
            using var downloadStep = await host.SendAsync(HttpMethod.Post, "/Setup/Progress", Token(storageHtml, ("SetupStepTypeId", "9")));
            Assert.AreEqual("/Setup/DownloadStorage", downloadStep.Headers.Location!.OriginalString);
            using var earlierStep = await host.SendAsync(HttpMethod.Post, "/Setup/Progress", Token(storageHtml, ("SetupStepTypeId", "2")));
            using var downloadResume = await host.SendAsync(HttpMethod.Get, "/Setup/Progress");
            Assert.AreEqual("/Setup/DownloadStorage", downloadResume.Headers.Location!.OriginalString);
            using var downloadPage = await host.SendAsync(HttpMethod.Get, "/Setup/DownloadStorage");
            using var sourceStep = await host.SendAsync(HttpMethod.Post, "/Setup/DownloadStorage?handler=Next", Token(await downloadPage.Content.ReadAsStringAsync()));
            Assert.AreEqual("/Setup/Sources", sourceStep.Headers.Location!.OriginalString);
            using var sourceResume = await host.SendAsync(HttpMethod.Get, "/Setup/Progress");
            Assert.AreEqual("/Setup/Sources", sourceResume.Headers.Location!.OriginalString);

            // Source editors remain inside the Sources step and keep their links inside the wizard.
            using var profiles = await host.SendAsync(HttpMethod.Get, "/Setup/Profiles");
            var profilesHtml = await profiles.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, profiles.StatusCode, profilesHtml);
            StringAssert.Contains(profilesHtml, "setup-wizard");
            StringAssert.Contains(profilesHtml, "href=\"/Setup/Profiles/New?type=Movie\"");

            // Missing essentials block the end; once nothing blocks, the wizard closes.
            using var blocked = await host.SendAsync(HttpMethod.Get, "/Setup/Sources");
            var blockedHtml = await blocked.Content.ReadAsStringAsync();
            StringAssert.Contains(blockedHtml, "Movies and series need a TMDB key.");
            await host.Fixture.RunSqlAsync("UPDATE \"MediaTypes\" SET \"IsEnabled\" = FALSE; UPDATE \"MediaSubtypes\" SET \"IsEnabled\" = FALSE");
            using var ready = await host.SendAsync(HttpMethod.Get, "/Setup/Sources");
            var readyHtml = await ready.Content.ReadAsStringAsync();
            StringAssert.Contains(readyHtml, "has no indexer yet.");
            var version = Regex.Match(readyHtml, "name=\"Version\" value=\"(\\d+)\"").Groups[1].Value;
            using var savedKey = await host.SendAsync(HttpMethod.Post, "/Setup/Sources", Token(readyHtml, ("Version", version), ("TmdbKey", "setup-key"), ("CompleteSetup", "false")));
            Assert.AreEqual("/Setup/Sources", savedKey.Headers.Location!.OriginalString, await savedKey.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"CompletedAt\" IS NULL FROM \"InstanceSetupState\""));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"ProtectedSecret\" IS NOT NULL FROM \"Providers\" WHERE \"Key\" = 'tmdb'"));
            using var persisted = await host.SendAsync(HttpMethod.Get, "/Setup/Sources");
            var persistedHtml = await persisted.Content.ReadAsStringAsync();
            Assert.IsFalse(persistedHtml.Contains("setup-key", StringComparison.Ordinal));
            StringAssert.Contains(persistedHtml, "aria-current=\"page\">Metadata");
            using var finished = await host.SendAsync(HttpMethod.Post, "/Setup/Sources", Token(readyHtml, ("Version", version)));
            Assert.AreEqual("/", finished.Headers.Location!.OriginalString);
            using var closed = await host.SendAsync(HttpMethod.Get, "/Setup/Progress");
            Assert.AreEqual("/", closed.Headers.Location!.OriginalString);
            using var late = await host.SendAsync(HttpMethod.Post, "/Setup/Progress", Token(instanceHtml, ("SetupStepTypeId", "3")));
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
            StringAssert.Contains(await state.Content.ReadAsStringAsync(), "\"requiresOwner\":false");
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
            Assert.AreEqual("/Setup/Storage", saved.Headers.Location!.OriginalString);
            Assert.AreEqual("My server", await host.ScalarAsync("SELECT \"Name\" FROM \"InstanceSettings\""));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'Devices'"));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'Registration'"));
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"MediaTypes\" WHERE \"Key\" = 'Music'"));
            Assert.AreEqual(movieVersion, await host.ScalarAsync("SELECT \"Version\" FROM \"MediaTypes\" WHERE \"Key\" = 'Movie'"));

            using var nextPage = await host.SendAsync(HttpMethod.Get, "/Setup/Storage");
            var nextHtml = await nextPage.Content.ReadAsStringAsync();
            var version = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"InstanceSetupState\""))!;
            using var next = await host.SendAsync(HttpMethod.Post, "/Setup/Storage?handler=Next", Token(nextHtml, ("Version", version.ToString())));
            Assert.AreEqual("/Setup/DownloadStorage", next.Headers.Location!.OriginalString, await next.Content.ReadAsStringAsync());
            using var downloadStorage = await host.SendAsync(HttpMethod.Get, "/Setup/DownloadStorage");
            using var proceed = await host.SendAsync(HttpMethod.Post, "/Setup/DownloadStorage?handler=Next", Token(await downloadStorage.Content.ReadAsStringAsync()));
            Assert.AreEqual("/Setup/Sources", proceed.Headers.Location!.OriginalString);
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"MediaTypes\" WHERE \"IsEnabled\""));
            Assert.AreEqual(0L, await host.ScalarAsync("SELECT count(*) FROM \"MediaSubtypes\" WHERE \"IsEnabled\""));
            using var sources = await host.SendAsync(HttpMethod.Get, "/Setup/Sources");
            var sourcesHtml = await sources.Content.ReadAsStringAsync();
            var current = (long)(await host.ScalarAsync("SELECT \"Version\" FROM \"InstanceSetupState\""))!;
            using var finish = await host.SendAsync(HttpMethod.Post, "/Setup/Sources", Token(sourcesHtml, ("Version", current.ToString()), ("TmdbKey", "")));
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
            using var completed = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/setup/sources", new { version, tmdbKey = "test-tmdb-key" });
            Assert.AreEqual(HttpStatusCode.OK, completed.StatusCode, await completed.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"CompletedAt\" IS NOT NULL FROM \"InstanceSetupState\""));
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"ProtectedSecret\" IS NOT NULL FROM \"Providers\" WHERE \"Key\" = 'tmdb'"));
            using var replay = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/setup/sources", new { version, tmdbKey = "replacement" });
            Assert.AreEqual(HttpStatusCode.BadRequest, replay.StatusCode);
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
