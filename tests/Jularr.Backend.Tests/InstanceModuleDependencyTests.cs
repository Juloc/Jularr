using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Public.Authentication.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    // A module builds on others: it is on only while they are, and they stay on while it is. The page shows and saves it as one step.
    [TestClass]
    public sealed class InstanceModuleDependencyTests
    {
        [TestMethod]
        public async Task Modules_Read_NameWhatEachNeeds()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            var modules = await ReadAsync(host);
            CollectionAssert.AreEqual(Array.Empty<string>(), modules.Single(module => module.Key == "Library").Requires);
            CollectionAssert.AreEqual(new[] { "Library" }, modules.Single(module => module.Key == "Playback").Requires);
            CollectionAssert.AreEqual(new[] { "Playback" }, modules.Single(module => module.Key == "Trickplay").Requires);
            // The full catalog is a sub-option of the Caching module.
            CollectionAssert.AreEqual(new[] { "DiscoverCache" }, modules.Single(module => module.Key == "LocalDiscovery").Requires);
            Assert.IsTrue(modules.Single(module => module.Key == "DiscoverCache").IsEnabled);

            // AniList is its own module on top of the library alone (its account part also needs the progress sync when called), and off until the Owner switches it on.
            CollectionAssert.AreEqual(new[] { "Library" }, modules.Single(module => module.Key == "AniList").Requires);
            Assert.IsFalse(modules.Single(module => module.Key == "AniList").IsEnabled);
        }

        [TestMethod]
        public async Task AniList_SwitchedOnWithoutProgressSync_IsAllowedAndLibraryStaysOnWhileItIs()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            var modules = await ReadAsync(host);
            var aniList = modules.Single(module => module.Key == "AniList");
            Assert.IsFalse(modules.Single(module => module.Key == "ProgressSync").IsEnabled);

            using var on = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/" + aniList.Id, new UpdateInstanceModuleDataV1(true, aniList.Version));
            Assert.AreEqual(HttpStatusCode.OK, on.StatusCode, await on.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'AniList'"));
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'ProgressSync'"));

            // The library stays on while AniList builds on it.
            var library = modules.Single(module => module.Key == "Library");
            using var blocked = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/" + library.Id, new UpdateInstanceModuleDataV1(false, library.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, blocked.StatusCode);
            StringAssert.Contains(await blocked.Content.ReadAsStringAsync(), "module_dependency");
        }

        [TestMethod]
        public async Task Module_SwitchedOnWhileWhatItNeedsIsOff_IsRefusedAndChangesNothing()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" IN ('Trickplay', 'Playback')");
            var modules = await ReadAsync(host);
            var trickplay = modules.Single(module => module.Key == "Trickplay");

            // Trickplay needs Playback, which is off.
            using var refused = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/" + trickplay.Id, new UpdateInstanceModuleDataV1(true, trickplay.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
            StringAssert.Contains(await refused.Content.ReadAsStringAsync(), "module_dependency");
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'Trickplay'"));

            // Playback first, then Trickplay.
            var playback = modules.Single(module => module.Key == "Playback");
            using var first = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/" + playback.Id, new UpdateInstanceModuleDataV1(true, playback.Version));
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
            using var second = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/" + trickplay.Id, new UpdateInstanceModuleDataV1(true, trickplay.Version));
            Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, await second.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task Module_SwitchedOffWhileAnotherNeedsIt_IsRefusedUntilTheOtherIsOff()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            var modules = await ReadAsync(host);
            var library = modules.Single(module => module.Key == "Library");
            Assert.IsTrue(modules.Any(module => module.Key == "Playback" && module.IsEnabled));

            using var refused = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/" + library.Id, new UpdateInstanceModuleDataV1(false, library.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'Library'"));
        }

        [TestMethod]
        public async Task Modules_SavedTogether_ChangeAModuleAndWhatItNeedsInOneStepOrNotAtAll()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            var modules = await ReadAsync(host);

            // Library and everything that builds on it go off together; the step is consistent, so it saves.
            var dependants = modules.Where(module => module.Requires.Length > 0 && module.IsEnabled).Select(module => module.Key).ToHashSet();
            var off = new HashSet<string>(modules.Where(module => module.Key == "Library" || module.Requires.Contains("Library") || dependants.Contains(module.Key)).Select(module => module.Key));
            var allOff = Input(modules, module => !off.Contains(module.Key) && module.IsEnabled);
            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules", allOff);
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'Library'"));

            // Playback on while Library stays off: refused, and the whole step rolls back, even for the modules that were fine.
            var after = await ReadAsync(host);
            var inconsistent = Input(after, module => module.Key is "Playback" or "Requests");
            using var refused = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules", inconsistent);
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'Playback'"));
            Assert.AreEqual(after.Single(module => module.Key == "Requests").IsEnabled, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'Requests'"));

            // Library with Playback together is consistent again.
            var again = Input(after, module => module.Key is "Library" or "Playback");
            using var restored = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules", again);
            Assert.AreEqual(HttpStatusCode.OK, restored.StatusCode, await restored.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task ModulesPage_ShowsWhatEachNeeds_AndSavesEveryChangedSwitchWithOneRequest()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Modules");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "data-module=\"Trickplay\" data-requires=\"Playback\"");
            StringAssert.Contains(html, "data-needed-names");

            // The Library card cannot move while others need it; its tags name them.
            var library = Regex.Match(html, "<div class=\"setting-card[^\"]*\"[^>]*data-module=\"Library\".*?data-module=\"Requests\"", RegexOptions.Singleline).Value;
            StringAssert.Contains(library, "setting-card--blocked");

            // One POST carries every switch, several changed at once.
            var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
            var form = Form(html, csrf, ("Subtitles", true), ("Uploads", true), ("Trickplay", true));
            using var saved = await host.SendAsync(HttpMethod.Post, "/Admin/Instance/Modules", form);
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            foreach (var key in new[] { "Subtitles", "Uploads", "Trickplay" })
            {
                Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = '" + key + "'"), key);
            }

            // A form that turns Library off while Playback stays on answers with the page, not with a partial save.
            using var reloaded = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Modules");
            var inconsistent = Form(await reloaded.Content.ReadAsStringAsync(), csrf, ("Library", false));
            using var refused = await host.SendAsync(HttpMethod.Post, "/Admin/Instance/Modules", inconsistent);
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'Library'"));
        }

        [TestMethod]
        public async Task Caching_SettingsSaveWithTheModules_WhetherTheModuleIsOnOrNot_WithinTheirRange()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            var modules = await ReadAsync(host);
            Assert.AreEqual("14:false", await host.ScalarAsync("SELECT \"CatalogDetailKeepDays\" || ':' || \"DetailsDiscoverAhead\" FROM \"InstanceSettings\" WHERE \"Id\" = 1"));

            using var saved = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules", Input(modules, module => module.IsEnabled, 30, true));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.AreEqual("30:true", await host.ScalarAsync("SELECT \"CatalogDetailKeepDays\" || ':' || \"DetailsDiscoverAhead\" FROM \"InstanceSettings\" WHERE \"Id\" = 1"));

            // Days out of range are refused with the whole step.
            foreach (var days in new[] { 0, 366 })
            {
                var current = await ReadAsync(host);
                using var refused = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules", Input(current, module => module.IsEnabled, days, false));
                Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode, days.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            Assert.AreEqual("30:true", await host.ScalarAsync("SELECT \"CatalogDetailKeepDays\" || ':' || \"DetailsDiscoverAhead\" FROM \"InstanceSettings\" WHERE \"Id\" = 1"));

            // With Caching off the days are still saved; the full catalog cannot stay on without it.
            var after = await ReadAsync(host);
            using var off = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules", Input(after, module => module.IsEnabled && module.Key is not ("DiscoverCache" or "LocalDiscovery"), 7, false));
            Assert.AreEqual(HttpStatusCode.OK, off.StatusCode, await off.Content.ReadAsStringAsync());
            Assert.AreEqual("7:false", await host.ScalarAsync("SELECT \"CatalogDetailKeepDays\" || ':' || \"DetailsDiscoverAhead\" FROM \"InstanceSettings\" WHERE \"Id\" = 1"));
            var lean = await ReadAsync(host);
            using var orphan = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules", Input(lean, module => module.IsEnabled || module.Key == "LocalDiscovery", 7, false));
            Assert.AreEqual(HttpStatusCode.BadRequest, orphan.StatusCode);
            StringAssert.Contains(await orphan.Content.ReadAsStringAsync(), "module_dependency");
        }

        [TestMethod]
        public async Task ModulesPage_ShowsCachingWithItsSettingsAndSubOptions_AndSavesThemWithTheSwitches()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Modules");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);

            // One card holds the module, its days with the one tooltip and its sub-options; the full catalog has no card of its own.
            var start = Regex.Match(html, "<div class=\"setting-card[^\"]*\"[^>]*data-module=\"DiscoverCache\"");
            Assert.IsTrue(start.Success, html);
            var next = Regex.Match(html[(start.Index + 1)..], "<div class=\"setting-card[ \"]");
            var card = next.Success ? html.Substring(start.Index, next.Index + 1) : html[start.Index..];
            StringAssert.Contains(card, ">Caching<");
            StringAssert.Contains(card, "Keep extended data");
            StringAssert.Matches(card, new Regex("name=\"CatalogDetailKeepDays\"[^>]*value=\"14\"|value=\"14\"[^>]*name=\"CatalogDetailKeepDays\""));
            StringAssert.Contains(card, "Seasons and episodes, cast, trailers, descriptions and relations of titles outside the library. Titles in the library are never cleaned up.");
            var subs = card[card.IndexOf("class=\"option-subs\"", StringComparison.Ordinal)..];
            StringAssert.Contains(subs, "data-module=\"LocalDiscovery\" data-requires=\"DiscoverCache\"");
            StringAssert.Contains(subs, "Keep full catalog");
            StringAssert.Contains(subs, "Detail Discover in advance");
            StringAssert.Contains(subs, "name=\"DetailsDiscoverAhead\"");
            Assert.AreEqual(1, Regex.Matches(html, "data-module=\"LocalDiscovery\"").Count);
            Assert.IsFalse(Regex.IsMatch(html, "class=\"setting-card[^\"]*\"[^>]*data-module=\"LocalDiscovery\""), "The full catalog is no card of its own.");
            Assert.AreEqual(1, Regex.Matches(html, "class=\"ui-field__tip\"").Count, "Only the one tooltip.");

            // The pill saves the sub-option and the settings with the switches.
            var csrf = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
            var form = Form(html, csrf, ("LocalDiscovery", true));
            form["CatalogDetailKeepDays"] = "21";
            form["DetailsDiscoverAhead"] = "true";
            using var saved = await host.SendAsync(HttpMethod.Post, "/Admin/Instance/Modules", form);
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            Assert.AreEqual(true, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'LocalDiscovery'"));
            Assert.AreEqual("21:true", await host.ScalarAsync("SELECT \"CatalogDetailKeepDays\" || ':' || \"DetailsDiscoverAhead\" FROM \"InstanceSettings\" WHERE \"Id\" = 1"));

            // Shown again as saved.
            using var again = await host.SendAsync(HttpMethod.Get, "/Admin/Instance/Modules");
            var shown = await again.Content.ReadAsStringAsync();
            StringAssert.Matches(shown, new Regex("<input[^>]*type=\"checkbox\"[^>]*name=\"DetailsDiscoverAhead\"[^>]*checked=\"checked\"|<input[^>]*checked=\"checked\"[^>]*name=\"DetailsDiscoverAhead\""));
        }

        // The form of the page as the browser would send it, with the given switches changed.
        internal static Dictionary<string, string> Form(string html, string csrf, params (string Key, bool IsEnabled)[] changes)
        {
            var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf };
            foreach (Match field in Regex.Matches(html, "name=\"(Modules\\[\\d+\\]\\.(?:Id|Version))\" value=\"(\\d+)\""))
            {
                form[field.Groups[1].Value] = field.Groups[2].Value;
            }

            // The Caching module's settings as shown.
            form["CatalogDetailKeepDays"] = Regex.Match(Regex.Match(html, "<input[^>]*name=\"CatalogDetailKeepDays\"[^>]*>").Value, "value=\"(\\d+)\"").Groups[1].Value;
            form["DetailsDiscoverAhead"] = Regex.Match(html, "<input[^>]*type=\"checkbox\"[^>]*name=\"DetailsDiscoverAhead\"[^>]*>").Value.Contains("checked=\"checked\"", StringComparison.Ordinal)
                ? "true" : "false";

            // Each switch as the page shows it.
            foreach (Match toggle in Regex.Matches(html, "<input[^>]*type=\"checkbox\"[^>]*name=\"(Modules\\[\\d+\\]\\.IsEnabled)\"[^>]*>"))
            {
                form[toggle.Groups[1].Value] = toggle.Value.Contains("checked=\"checked\"", StringComparison.Ordinal) ? "True" : "False";
            }

            // The card of a module holds its switch.
            foreach (var (key, isEnabled) in changes)
            {
                var switchName = Regex.Match(html, "data-module=\"" + key + "\".*?name=\"(Modules\\[\\d+\\]\\.IsEnabled)\"", RegexOptions.Singleline).Groups[1].Value;
                form[switchName] = isEnabled ? "True" : "False";
            }

            return form;
        }
        private static UpdateInstanceModulesDataV1 Input(IReadOnlyList<InstanceModuleDetailsV1> modules, Func<InstanceModuleDetailsV1, bool> isOn, int keepDays = 14, bool detailsAhead = false) =>
            new(new BaseDataList<InstanceModuleInputV1>(modules.Select(module => new InstanceModuleInputV1(module.Id, isOn(module), module.Version))), keepDays, detailsAhead);

        private static async Task<IReadOnlyList<InstanceModuleDetailsV1>> ReadAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/instance/modules");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<BaseDataList<InstanceModuleDetailsV1>>())!;
        }

        private static async Task SignInAsync(Host host)
        {
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        }
    }
}
