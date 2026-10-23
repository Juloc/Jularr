using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The settings pages in a real browser: the one save pill for several changes, switches that reload nothing and the dependencies between modules.
    [TestClass]
    public sealed class SettingsPagesTests
    {
        private const string Marker = "window.settingsMarker = 'same document'";

        [TestMethod]
        [Timeout(300_000)]
        public async Task Modules_DependenciesBlockSwitches_AndSeveralChangesSaveTogetherWithoutReload()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Instance/Modules");
            await page.EvaluateAsync(Marker);
            var pill = page.Locator("[data-save-pill]");
            ILocator Card(string key) => page.Locator(".setting-card[data-module=" + key + "]");
            ILocator Switch(string key) => Card(key).Locator("input[type=checkbox]");

            // Library is needed by modules that are on: its card names them and its switch does not move.
            await Assertions.Expect(Card("Library")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("setting-card--blocked"));
            await Assertions.Expect(Card("Library").Locator("[data-needed-names]")).ToContainTextAsync("Playback");
            await Card("Library").Locator(".toggle").ClickAsync(new() { Force = true });
            await Assertions.Expect(Switch("Library")).ToBeCheckedAsync();
            await Assertions.Expect(pill).ToBeHiddenAsync();

            // What a module needs shows on its card; Translation needs the reader, which is on.
            await Assertions.Expect(Card("Translation").Locator("[data-needs=Reading]")).ToBeVisibleAsync();
            await Card("Subtitles").Locator(".toggle").ClickAsync();
            await Card("Translation").Locator(".toggle").ClickAsync();
            await Card("Trickplay").Locator(".toggle").ClickAsync();
            await Assertions.Expect(pill).ToContainTextAsync("3 changes");

            // The reader is needed now by the draft; it stays on until Translation is off again.
            await Card("Reading").Locator(".toggle").ClickAsync(new() { Force = true });
            await Assertions.Expect(Switch("Reading")).ToBeCheckedAsync();
            await Assertions.Expect(Card("Reading").Locator("[data-needed-names]")).ToContainTextAsync("Translate books");
            await Card("Translation").Locator(".toggle").ClickAsync();
            await Assertions.Expect(pill).ToContainTextAsync("2 changes");
            await Evidence.SaveAsync(page, "modules-draft");

            // One save for both, no reload of the page.
            await Steps.SaveAsync(page);
            Assert.AreEqual("same document", await page.EvaluateAsync<string>("window.settingsMarker"));
            await page.ReloadAsync();
            await Assertions.Expect(Switch("Subtitles")).ToBeCheckedAsync();
            await Assertions.Expect(Switch("Trickplay")).ToBeCheckedAsync();
            await Assertions.Expect(Switch("Translation")).Not.ToBeCheckedAsync();

            // Turning a module off that nothing needs is free; a dependant that is off lets its module go.
            await Card("Trickplay").Locator(".toggle").ClickAsync();
            await Card("Playback").Locator(".toggle").ClickAsync();
            await Assertions.Expect(pill).ToContainTextAsync("2 changes");
            await Steps.SaveAsync(page);
            await page.ReloadAsync();
            await Assertions.Expect(Switch("Playback")).Not.ToBeCheckedAsync();
            await Assertions.Expect(Card("Trickplay").Locator("[data-needs=Playback]")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("tag--warning"));
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task Instance_ChangesInSeveralSections_SaveWithOneSave()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Instance");
            await page.EvaluateAsync(Marker);
            var pill = page.Locator("[data-save-pill]");
            await Assertions.Expect(page.Locator("fieldset.ui-form__section")).ToHaveCountAsync(4);
            await Assertions.Expect(page.Locator("[data-form-save]").First).ToBeHiddenAsync();

            // A name, a slider and the trusted proxies sit in three sections of the same form.
            await page.Locator("[name=Name]").FillAsync("Settings test");
            await page.Locator("[name=BackupKeep]").FillAsync("21");
            await Assertions.Expect(page.Locator(".ui-field", new() { Has = page.Locator("[name=BackupKeep]") }).Locator("output")).ToHaveTextAsync("21");
            await page.Locator("[name=TrustedProxies]").FillAsync("10.0.0.0/8");
            await Assertions.Expect(pill).ToContainTextAsync("3 changes");
            await Evidence.SaveAsync(page, "instance-sections");
            await Steps.SaveAsync(page);
            Assert.AreEqual("same document", await page.EvaluateAsync<string>("window.settingsMarker"));
            await Assertions.Expect(pill.GetByRole(AriaRole.Button, new() { Name = "Saved", Exact = true })).ToBeVisibleAsync();

            await page.ReloadAsync();
            await Assertions.Expect(page.Locator("[name=Name]")).ToHaveValueAsync("Settings test");
            await Assertions.Expect(page.Locator("[name=BackupKeep]")).ToHaveValueAsync("21");
            await Assertions.Expect(page.Locator("[name=TrustedProxies]")).ToHaveValueAsync("10.0.0.0/8");

            // Settings of a module that is off are not there: nothing about collections or subtitles.
            await Assertions.Expect(page.Locator("[name=SubtitleLanguages]")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("[name=PublicUrl]:not([type=hidden])")).ToHaveCountAsync(0);
            await Steps.SetModuleAsync(page, "Subtitles from providers", true);
            await page.GotoAsync("/Admin/Instance");
            await Assertions.Expect(page.Locator("[name=SubtitleLanguages]")).ToHaveCountAsync(1);
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task MediaTypes_SwitchesAreDraftsOfOnePill_AndSaveWithoutReload()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Instance/MediaTypes");
            await page.EvaluateAsync(Marker);
            var pill = page.Locator("[data-save-pill]");
            ILocator Switch(string name) => page.Locator(".setting-card", new() { Has = page.Locator(".setting-card__name", new() { HasText = name }) }).Locator("input[type=checkbox]");
            ILocator Toggle(string name) => page.Locator(".setting-card", new() { Has = page.Locator(".setting-card__name", new() { HasText = name }) }).Locator(".toggle");

            await Assertions.Expect(page.Locator(".setting-card")).ToHaveCountAsync(8);
            var manga = await Switch("Manga").IsCheckedAsync();
            var music = await Switch("Music").IsCheckedAsync();
            await Toggle("Manga").ClickAsync();
            await Toggle("Music").ClickAsync();
            await Assertions.Expect(pill).ToContainTextAsync("2 changes");
            await Steps.SaveAsync(page);
            Assert.AreEqual("same document", await page.EvaluateAsync<string>("window.settingsMarker"));
            await page.ReloadAsync();
            Assert.AreEqual(!manga, await Switch("Manga").IsCheckedAsync());
            Assert.AreEqual(!music, await Switch("Music").IsCheckedAsync());
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task Languages_AddInADialogWithCancel_AndEditInlineInTheCardSavedByThePill()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Instance/Languages");
            await page.EvaluateAsync(Marker);
            ILocator Card(string name) => page.Locator(".feature-card", new() { HasText = name });
            await Assertions.Expect(Card("English").First).ToBeVisibleAsync();

            // Adding stays a dialog: Cancel leaves nothing behind, adding creates the card.
            var add = page.GetByRole(AriaRole.Dialog, new() { Name = "Add language", Exact = true });
            await page.GetByRole(AriaRole.Button, new() { Name = "Add language", Exact = true }).ClickAsync();
            await add.Locator("[name=Locale]").FillAsync("xx-XX");
            await add.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Assertions.Expect(add).ToBeHiddenAsync();
            await Assertions.Expect(Card("xx-XX")).ToHaveCountAsync(0);
            await page.GetByRole(AriaRole.Button, new() { Name = "Add language", Exact = true }).ClickAsync();
            await add.Locator("[name=Locale]").FillAsync("fr-CA");
            await add.Locator("[name=Name]").FillAsync("French (CA)");
            await Steps.FollowAsync(page, add.GetByRole(AriaRole.Button, new() { Name = "Add language", Exact = true }));
            await Assertions.Expect(Card("French (CA)")).ToContainTextAsync("Enabled");

            // Editing is in the card itself, with no Edit button, dialog or drawer; the pill saves it.
            var card = Card("French (CA)");
            Assert.AreEqual(0, await page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).CountAsync());
            await card.Locator("[name=Name]").FillAsync("Canadian French");
            await card.Locator(".toggle").ClickAsync();
            var pill = page.Locator("[data-save-pill]");
            await Assertions.Expect(pill).ToContainTextAsync("2 changes");
            await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
            await Steps.SaveAsync(page);
            await Assertions.Expect(Card("Canadian French")).ToContainTextAsync("Disabled");
            await Assertions.Expect(pill).ToBeHiddenAsync();
            Assert.AreEqual("same document", await page.EvaluateAsync<string>("window.settingsMarker"));
            await page.ReloadAsync();
            await Assertions.Expect(Card("Canadian French").Locator("[name=Name]")).ToHaveValueAsync("Canadian French");
            await Assertions.Expect(Card("Canadian French").Locator("input[name=IsEnabled][type=checkbox]")).Not.ToBeCheckedAsync();
            await Evidence.SaveAsync(page, "languages-inline");
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task Naming_TokensFillTheFieldAndTheExampleFollowsWhatIsTyped()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Instance/Naming");
            var movie = page.Locator("form[data-form-kind$=UpdateNamingPatternDataV1]").First;
            var example = page.Locator("[data-naming-example]").First;
            var file = movie.Locator("[name=File]");

            // The example is the server's answer to what is typed, before anything is saved.
            await file.FillAsync("{Title} X{Year}");
            await Assertions.Expect(example).ToContainTextAsync("Example Movie X1999.mkv");
            await file.FillAsync("{Title} {Nothing}");
            await Assertions.Expect(example).ToHaveTextAsync("The pattern uses an unknown placeholder.");
            await Assertions.Expect(example).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("naming-example--problem"));

            // A placeholder of the drawer is written into the field that was used last.
            await file.FillAsync("{Title}");
            await page.GetByRole(AriaRole.Button, new() { Name = "Placeholders", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "{Year}", Exact = true }).ClickAsync();
            await Assertions.Expect(file).ToHaveValueAsync("{Title}{Year}");
            await Assertions.Expect(example).ToContainTextAsync("Example Movie1999.mkv");
            await Assertions.Expect(page.Locator("[data-save-pill]")).ToContainTextAsync("1 change");
            await Evidence.SaveAsync(page, "naming-page");
        }
    }
}
