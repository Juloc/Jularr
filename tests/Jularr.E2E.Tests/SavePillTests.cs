using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The page's one save pill: it counts what differs from the loaded page, discards in place and saves every changed form, keeping a failing form's error.
    [TestClass]
    public sealed class SavePillTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task NamingPage_SavesChangedFormsTogether_DiscardsInPlace_AndKeepsAFailingFormsError()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            var asked = false;
            page.Dialog += (_, dialog) => { asked = true; _ = dialog.DismissAsync(); };
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Admin/Instance/Naming");
            var pill = page.Locator("[data-save-pill]");
            var save = pill.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true });
            var file = page.Locator("form[data-form-kind$=UpdateNamingPatternDataV1] [name=File]").First;
            var replaces = page.Locator("form[data-form-kind$=UpdateNamingSettingsDataV1] [name=ReplacesIllegalCharacters]").First;
            var toggle = page.Locator("form[data-form-kind$=UpdateNamingSettingsDataV1] .toggle").First;
            await Assertions.Expect(pill).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("[data-form-save]").First).ToBeHiddenAsync();

            // Two changes show in the pill; discarding restores the saved page without asking.
            var original = await file.InputValueAsync();
            await file.FillAsync("{Title} ({Year}) pill");
            await toggle.ClickAsync();
            await Assertions.Expect(pill).ToContainTextAsync("2 changes");
            await Evidence.SaveAsync(page, "save-pill-desktop");
            await page.SetViewportSizeAsync(390, 844);
            await Evidence.SaveAsync(page, "save-pill-mobile");
            await page.SetViewportSizeAsync(1280, 720);
            await Steps.FollowAsync(page, pill.GetByRole(AriaRole.Button, new() { Name = "Discard", Exact = true }));
            await Assertions.Expect(pill).ToBeHiddenAsync();
            await Assertions.Expect(file).ToHaveValueAsync(original);
            await Assertions.Expect(replaces).ToBeCheckedAsync();

            // An empty file name fails on its own form; the other form is saved all the same.
            await file.FillAsync("");
            await toggle.ClickAsync();
            await Steps.FollowAsync(page, save);
            await Assertions.Expect(pill).ToContainTextAsync("1 not saved");
            await Assertions.Expect(page.Locator("form[data-save] [role=alert]")).ToHaveCountAsync(1);
            await Assertions.Expect(replaces).Not.ToBeCheckedAsync();

            // The corrected name is saved; a fresh load shows both values.
            await file.FillAsync("{Title} ({Year}) pill");
            await Steps.FollowAsync(page, save);
            await Assertions.Expect(pill.GetByRole(AriaRole.Button, new() { Name = "Saved", Exact = true })).ToBeVisibleAsync();
            await page.ReloadAsync();
            await Assertions.Expect(file).ToHaveValueAsync("{Title} ({Year}) pill");
            await Assertions.Expect(replaces).Not.ToBeCheckedAsync();

            // The profile settings save the same way.
            await page.GotoAsync("/Settings");
            var displayName = page.Locator("form[data-save] [name=DisplayName]");
            await displayName.FillAsync("Pill profile");
            await Steps.SaveAsync(page);
            await page.ReloadAsync();
            await Assertions.Expect(displayName).ToHaveValueAsync("Pill profile");
            Assert.IsFalse(asked, "Discarding or saving asked before leaving.");
        }

        // The settings pages use the shared controls: a styled dropdown instead of the system's list, round boxes, a number that steps, a slider that
        // shows its number and a home order that is dragged; every change is counted by the one dock, which saves them together.
        [TestMethod]
        [Timeout(300_000)]
        public async Task SettingsPage_SharedControls_DropdownBoxesStepperSliderAndDraggedOrder_AreCountedBySaveDock()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await page.GotoAsync("/Settings");
            var dock = page.Locator("[data-save-pill]");
            await Assertions.Expect(dock).ToBeHiddenAsync();

            // The language is the styled dropdown: the system's select stays behind it, its list opens as a popup and a choice is a change.
            var language = page.Locator("main .ui-field", new() { Has = page.Locator("select[name=UiLocaleId]") });
            var button = language.Locator(".ui-dropdown__button");
            await Assertions.Expect(button).ToBeVisibleAsync();
            Assert.IsFalse(await language.Locator("select").EvaluateAsync<bool>("select => select.getBoundingClientRect().width > 2"), "The system's select shows.");
            Assert.AreEqual("11px", await language.Locator(".ui-field__control").EvaluateAsync<string>("control => getComputedStyle(control).borderRadius"));
            await button.ClickAsync();
            var list = page.Locator(".ui-dropdown__popup:popover-open");
            await Assertions.Expect(list.GetByRole(AriaRole.Option)).ToHaveCountAsync(2);
            await list.GetByRole(AriaRole.Option, new() { Name = "Deutsch" }).ClickAsync();
            await Assertions.Expect(list).ToBeHiddenAsync();
            await Assertions.Expect(button).ToContainTextAsync("Deutsch");
            await Assertions.Expect(dock).ToContainTextAsync("1 change");
            await button.ClickAsync();
            await Assertions.Expect(list).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("ArrowDown");
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(button).ToContainTextAsync("English");
            await Assertions.Expect(dock).ToBeHiddenAsync();

            // A box is the shared round-cornered box, not the browser's.
            var movie = page.Locator("input[name=HiddenKeys][value=Movie]");
            Assert.AreEqual("none", await movie.EvaluateAsync<string>("box => getComputedStyle(box).appearance"));
            await movie.CheckAsync();
            await Assertions.Expect(dock).ToContainTextAsync("1 change");
            await movie.UncheckAsync();
            await Assertions.Expect(dock).ToBeHiddenAsync();

            // The slider shows its number while it moves.
            var rate = page.Locator("input[type=range][name=Rate]");
            var number = rate.Locator("xpath=following-sibling::output");
            await rate.FillAsync("1.5");
            await Assertions.Expect(number).ToHaveTextAsync("1.5");
            await Assertions.Expect(dock).ToContainTextAsync("1 change");
            await rate.FillAsync("0.5");
            await Assertions.Expect(number).ToHaveTextAsync("0.5");

            // The home page order is dragged by its handle; the numbers follow.
            var rows = page.Locator("[data-sortable-row]");
            var keys = () => rows.EvaluateAllAsync<string[]>("rows => rows.map(row => row.dataset.key)");
            await page.Locator("[data-sortable]").EvaluateAsync("list => list.scrollIntoView({ block: 'center' })");
            var before = await keys();
            var first = await rows.Nth(0).Locator("[data-sortable-handle]").BoundingBoxAsync();
            var third = await rows.Nth(2).BoundingBoxAsync();
            await page.Mouse.MoveAsync(first!.X + first.Width / 2, first.Y + first.Height / 2);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(first.X + first.Width / 2, first.Y + first.Height / 2 + 20, new() { Steps = 5 });
            await page.Mouse.MoveAsync(first.X + first.Width / 2, third!.Y + third.Height - 4, new() { Steps = 15 });
            await page.Mouse.UpAsync();
            var dragged = await keys();
            CollectionAssert.AreEqual(new[] { before[1], before[2], before[0] }, dragged[..3]);
            await Assertions.Expect(rows.Nth(2).Locator(".sortable__number")).ToHaveTextAsync("3");
            await Assertions.Expect(dock).ToContainTextAsync("2 changes");

            // The menu of a row moves it too, and the switch hides its shelf.
            await rows.Nth(0).Locator(".sortable__dots").ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new() { Name = "Move to bottom" }).ClickAsync();
            Assert.AreEqual(before[1], (await keys())[^1]);
            await rows.Nth(0).Locator(".toggle").ClickAsync();

            // Saved together, the order and the hidden shelf are still there after a reload.
            var expected = await keys();
            await Steps.SaveAsync(page);
            await page.ReloadAsync();
            var saved = await keys();
            CollectionAssert.AreEqual(expected, saved, "Expected " + string.Join(",", expected) + " but the page shows " + string.Join(",", saved));
            await Assertions.Expect(rows.Nth(0).Locator(".toggle input")).Not.ToBeCheckedAsync();
            await Assertions.Expect(rows.Nth(0)).ToContainTextAsync("Hidden");
            await Assertions.Expect(dock).ToBeHiddenAsync();

            // A number steps with its own buttons, and the dock counts it.
            await page.GotoAsync("/Admin/Instance");
            // A unit sits quietly at the end of its field, inside the frame, and does not take part in the value.
            var interval = page.Locator("input[name=HistoryRetentionDays]");
            await Assertions.Expect(interval.Locator("xpath=following-sibling::*[contains(@class,'ui-field__suffix')]")).ToHaveTextAsync("days");
            Assert.AreEqual(1, await interval.Locator("xpath=ancestor::div[contains(@class,'ui-field__control--affixed')]").CountAsync());
            var keep = page.Locator("input[name=HistoryMegabytes]");
            var value = int.Parse(await keep.InputValueAsync(), System.Globalization.CultureInfo.InvariantCulture);
            await keep.Locator("xpath=following-sibling::*[contains(@class,'ui-field__stepper')]").GetByRole(AriaRole.Button, new() { Name = "Increase value" }).ClickAsync();
            await Assertions.Expect(keep).ToHaveValueAsync((value + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            await Assertions.Expect(dock).ToContainTextAsync("1 change");
            Assert.AreEqual(0, browser.Problems.Count, string.Join(Environment.NewLine, browser.Problems));
        }
    }
}
