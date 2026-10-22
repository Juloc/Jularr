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
            CollectionAssert.AreEqual(new[] { "DiscoverCache" }, modules.Single(module => module.Key == "LocalDiscovery").Requires);

            // AniList is its own module on top of the library and the progress sync, and off until the Owner switches it on.
            CollectionAssert.AreEquivalent(new[] { "Library", "ProgressSync" }, modules.Single(module => module.Key == "AniList").Requires);
            Assert.IsFalse(modules.Single(module => module.Key == "AniList").IsEnabled);
        }

        [TestMethod]
        public async Task AniList_SwitchedOnWithoutProgressSync_IsRefusedUntilItIsOn()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await SignInAsync(host);
            var modules = await ReadAsync(host);
            var aniList = modules.Single(module => module.Key == "AniList");
            var progressSync = modules.Single(module => module.Key == "ProgressSync");
            Assert.IsFalse(progressSync.IsEnabled);

            using var refused = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/" + aniList.Id, new UpdateInstanceModuleDataV1(true, aniList.Version));
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
            StringAssert.Contains(await refused.Content.ReadAsStringAsync(), "module_dependency");
            Assert.AreEqual(false, await host.ScalarAsync("SELECT \"IsEnabled\" FROM \"InstanceModules\" WHERE \"Key\" = 'AniList'"));

            using var first = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/" + progressSync.Id, new UpdateInstanceModuleDataV1(true, progressSync.Version));
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
            using var second = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/" + aniList.Id, new UpdateInstanceModuleDataV1(true, aniList.Version));
            Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, await second.Content.ReadAsStringAsync());

            // The progress sync stays on while AniList builds on it.
            using var blocked = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/" + progressSync.Id, new UpdateInstanceModuleDataV1(false, progressSync.Version + 1));
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

        // The form of the page as the browser would send it, with the given switches changed.
        internal static Dictionary<string, string> Form(string html, string csrf, params (string Key, bool IsEnabled)[] changes)
        {
            var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf };
            foreach (Match field in Regex.Matches(html, "name=\"(Modules\\[\\d+\\]\\.(?:Id|Version))\" value=\"(\\d+)\""))
            {
                form[field.Groups[1].Value] = field.Groups[2].Value;
            }

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
        private static UpdateInstanceModulesDataV1 Input(IReadOnlyList<InstanceModuleDetailsV1> modules, Func<InstanceModuleDetailsV1, bool> isOn) =>
            new(new BaseDataList<InstanceModuleInputV1>(modules.Select(module => new InstanceModuleInputV1(module.Id, isOn(module), module.Version))));

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
