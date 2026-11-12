using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class ArrSettingsTests
    {
        // The list kit's row of an item, found by the words of its name.
        private static ILocator Row(IPage page, string text) => page.Locator(".list-body .list-row", new() { HasText = text });

        // The card of a release profile, found by the words of its name.
        private static ILocator Card(IPage page, string text) => page.Locator(".card-grid .feature-card", new() { HasText = text });

        [TestMethod]
        [Timeout(600_000)]
        public async Task FormatsProfilesAndTags_WorkThroughListsCardsAndDialogs_OnDesktopAndMobile()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);

                // One row of five tabs joins the quality rules, without a media-type bar, and no tab offers a waiting time; the groups add their rating filter to the row.
                foreach (var label in new[] { "Profiles", "Custom formats", "Release groups", "Release profiles", "Quality" })
                {
                    await page.GotoAsync("/Admin/Acquisition/Profiles");
                    await page.Locator(".navigation-tabs").GetByRole(AriaRole.Link, new() { Name = label, Exact = true }).ClickAsync();
                    await Assertions.Expect(page.Locator(".navigation-tabs")).ToHaveCountAsync(1);
                    await Assertions.Expect(page.Locator(".navigation-tabs__link")).ToHaveCountAsync(label == "Release groups" ? 10 : 5);
                    await Assertions.Expect(page.Locator("main .capsule-bar")).ToHaveCountAsync(0);
                    await Assertions.Expect(page.Locator("main [role=alert]")).ToHaveCountAsync(0);
                    var text = (await page.Locator("main").InnerTextAsync()).ToLowerInvariant();
                    Assert.IsFalse(text.Contains("delay", StringComparison.Ordinal) || text.Contains("wait", StringComparison.Ordinal), "A waiting time is offered on " + label);
                }

                // An address of the former panes leads to the tab that holds it now.
                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=groups&mediaTypeId=5");
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("/Admin/Acquisition/Profiles\\?tab=groups$"));
                await Assertions.Expect(page.Locator("#release-groups")).ToBeVisibleAsync();

                // A custom format with its conditions is a dialog of the list; it belongs to every media type.
                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=formats");
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "New format", Exact = true }));
                var format = page.Locator("dialog#format-editor form").First;
                await format.Locator("[name=Name]").FillAsync("Browser format");
                await format.Locator("[name='Conditions[0].Name']").FillAsync("Group");
                await Assertions.Expect(format.Locator("[data-form-refresh-submit]")).ToBeHiddenAsync();
                await page.EvaluateAsync("window.__arrDraftProbe = 42");
                await format.Locator("[name='Conditions[0].Kind']").SelectOptionAsync("8");
                await Assertions.Expect(format.Locator("[name='Conditions[0].Minimum']")).ToBeVisibleAsync();
                await Assertions.Expect(format.Locator("[name='Conditions[0].Value']")).ToHaveCountAsync(0);
                Assert.AreEqual("Browser format", await format.Locator("[name=Name]").InputValueAsync());
                Assert.AreEqual(42, await page.EvaluateAsync<int>("window.__arrDraftProbe"));
                await format.Locator("[name='Conditions[0].Kind']").SelectOptionAsync("0");
                await Assertions.Expect(format.Locator("[name='Conditions[0].Value']")).ToBeVisibleAsync();
                await Assertions.Expect(format.Locator("[name='Conditions[0].Minimum']")).ToHaveCountAsync(0);
                await format.Locator("[name='Conditions[0].Value']").FillAsync("TEAM");
                await format.GetByRole(AriaRole.Button, new() { Name = "Add condition", Exact = true }).ClickAsync();
                Assert.AreEqual("Browser format", await format.Locator("[name=Name]").InputValueAsync());
                await format.Locator("[name='Conditions[1].Name']").FillAsync("Title");
                await format.Locator("[name='Conditions[1].Value']").FillAsync("HDR");
                await Steps.SubmitAsync(page, format);
                await Assertions.Expect(page.Locator("main [role=alert]")).ToHaveCountAsync(0);
                await Assertions.Expect(Row(page, "Browser format")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator("dialog#format-editor[open]")).ToHaveCountAsync(0);

                // Its conditions come back when it is opened again.
                await Steps.FollowAsync(page, Row(page, "Browser format").GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }));
                await Assertions.Expect(page.Locator("dialog#format-editor [name='Conditions[1].Value']")).ToHaveValueAsync("HDR");
                await page.Locator("dialog#format-editor").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();

                // A new profile starts with its media type; the editor scores every format, and a score of the new format is kept.
                await page.GotoAsync("/Admin/Acquisition/Profiles");
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "New profile", Exact = true }));
                await Steps.FollowAsync(page, page.Locator("dialog#profile-new").GetByRole(AriaRole.Link, new() { Name = "Movies", Exact = true }));
                var profile = page.Locator("main form", new() { Has = page.Locator("[name=MinimumFormatScore]") });
                await Assertions.Expect(profile.Locator("[name=MediaKinds][value=Movie]")).ToBeCheckedAsync();
                await Assertions.Expect(profile.Locator("[name=MediaKinds][value=Book]")).ToHaveCountAsync(0);
                await profile.Locator("[name=Name]").FillAsync("Browser quality");
                await profile.Locator("details.profile-advanced > summary").ClickAsync();
                await profile.Locator(".ui-form__row", new() { HasText = "Browser format" }).Locator("input[name$='.Score']").FillAsync("100");
                await profile.Locator("[name=MinimumFormatScore]").FillAsync("100");
                await Steps.SaveAsync(page);
                await Assertions.Expect(page.Locator("main [role=alert]")).ToHaveCountAsync(0);
                await Assertions.Expect(profile.Locator(".ui-form__row", new() { HasText = "Browser format" }).Locator("input[name$='.Score']")).ToHaveValueAsync("100");

                // The allowed qualities are a short list: the round arrows move one (the first cannot move up), the choice below adds one, the cross takes it out again.
                var qualities = profile.Locator(".profile-quality");
                await Assertions.Expect(qualities.First.GetByRole(AriaRole.Button, new() { Name = "Move up", Exact = true })).ToBeDisabledAsync();
                var first = await qualities.First.Locator(".profile-quality__name").InnerTextAsync();
                await qualities.First.GetByRole(AriaRole.Button, new() { Name = "Move down", Exact = true }).ClickAsync();
                await Assertions.Expect(qualities.Nth(1).Locator(".profile-quality__name")).ToHaveTextAsync(first);
                var count = await qualities.CountAsync();
                await profile.Locator("[name=addQuality]").SelectOptionAsync("Bluray-2160p");
                await profile.GetByRole(AriaRole.Button, new() { Name = "Add quality", Exact = true }).ClickAsync();
                await Assertions.Expect(qualities).ToHaveCountAsync(count + 1);
                await Assertions.Expect(qualities.Last.Locator(".profile-quality__name")).ToHaveTextAsync("Bluray-2160p");
                await qualities.Last.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
                await Assertions.Expect(qualities).ToHaveCountAsync(count);
                await Assertions.Expect(profile.Locator("[name=CodecPreference]")).ToHaveCountAsync(3);

                // The profile also applies to anime and becomes its default: the list names both, and the series profile no longer is Anime's default.
                await profile.Locator("[name=MediaKinds][value=Anime]").CheckAsync(new() { Force = true });
                await profile.Locator("[name=DefaultFor][value=Anime]").CheckAsync(new() { Force = true });
                await Steps.SaveAsync(page);
                await page.GotoAsync("/Admin/Acquisition/Profiles");
                var created = Row(page, "Browser quality");
                await Assertions.Expect(created.Locator(".profile-kinds-cell").First).ToContainTextAsync("Movies");
                await Assertions.Expect(created.Locator(".tag--accent")).ToHaveTextAsync("Anime");
                await Assertions.Expect(page.Locator(".tag--accent", new() { HasTextString = "Anime" })).ToHaveCountAsync(1);

                // A release profile is added in a dialog with Cancel; afterwards its settings are inputs of its card, saved by the page's pill.
                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=releases");
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "New release profile", Exact = true }));
                Assert.AreEqual(1, await page.Locator("dialog#release-editor").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).CountAsync());
                var release = page.Locator("dialog#release-editor form").First;
                await release.Locator("[name=Name]").FillAsync("Browser release");
                await release.Locator("[name=Required]").FillAsync("/^TEAM{1,3},HDR$/i");
                await release.Locator("[name=Ignored]").FillAsync("x265\nCAM");
                await Steps.SubmitAsync(page, release);
                await Assertions.Expect(page.Locator("dialog#release-editor[open]")).ToHaveCountAsync(0);
                var releaseCard = Card(page, "Browser release");
                await Assertions.Expect(page.Locator("#release-profiles").Locator(".feature-card", new() { HasText = "Browser release" })).ToBeVisibleAsync();
                Assert.AreEqual("/^TEAM{1,3},HDR$/i", await releaseCard.Locator("[name=Required]").InputValueAsync());
                Assert.AreEqual("x265\nCAM", await releaseCard.Locator("[name=Ignored]").InputValueAsync());
                await Assertions.Expect(page.Locator("[data-save-pill]")).ToBeHiddenAsync();
                await releaseCard.Locator("[name=Ignored]").FillAsync("x265");
                await Assertions.Expect(page.Locator("[data-save-pill] .save-pill__count")).ToHaveTextAsync("1 change");
                await Steps.SaveAsync(page);
                await page.ReloadAsync();
                Assert.AreEqual("x265", await Card(page, "Browser release").Locator("[name=Ignored]").InputValueAsync());

                // The groups tab searches while typing; words that name no group offer to add it. A click on its rating saves it at once, without a reload.
                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=groups");
                await page.GetByRole(AriaRole.Searchbox, new() { Name = "Find a group" }).FillAsync("Browser-Group");
                await Assertions.Expect(page).ToHaveURLAsync(new Regex("q=Browser-Group"));
                await Steps.SubmitAsync(page, page.Locator("form", new() { Has = page.Locator(".release-groups-add") }).First);
                var groupRow = Row(page, "Browser-Group");
                await Assertions.Expect(groupRow).ToContainTextAsync("Not rated");
                await Assertions.Expect(groupRow.Locator("[data-form-submit-fallback]")).ToBeHiddenAsync();
                await page.EvaluateAsync("window.__groupProbe = 7");
                await Steps.FollowAsync(page, groupRow.Locator(".ui-segment", new() { HasText = "Good" }));
                await Assertions.Expect(Row(page, "Browser-Group").Locator("input[value='1']")).ToBeCheckedAsync();
                Assert.AreEqual(7, await page.EvaluateAsync<int>("window.__groupProbe"), "The rating reloaded the page.");
                await page.ReloadAsync();
                await Assertions.Expect(Row(page, "Browser-Group").Locator("input[value='1']")).ToBeCheckedAsync();
                await Assertions.Expect(Row(page, "Browser-Group")).Not.ToContainTextAsync("Not rated");

                // The chosen segment clicked again takes the rating back; by keyboard Enter does the same.
                await Steps.FollowAsync(page, Row(page, "Browser-Group").Locator(".ui-segment", new() { HasText = "Blocked" }));
                await Assertions.Expect(Row(page, "Browser-Group").Locator("input[value='3']")).ToBeCheckedAsync();
                await Steps.FollowAsync(page, Row(page, "Browser-Group").Locator(".ui-segment", new() { HasText = "Blocked" }));
                await Assertions.Expect(Row(page, "Browser-Group")).ToContainTextAsync("Not rated");
                await Assertions.Expect(Row(page, "Browser-Group").Locator("input:checked")).ToHaveCountAsync(0);
                await Steps.FollowAsync(page, Row(page, "Browser-Group").Locator(".ui-segment", new() { HasText = "Bad" }));
                await Assertions.Expect(Row(page, "Browser-Group").Locator("input[value='2']")).ToBeCheckedAsync();
                await Row(page, "Browser-Group").Locator("input[value='2']").FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await Assertions.Expect(Row(page, "Browser-Group").Locator("input:checked")).ToHaveCountAsync(0);

                // A tag is a card whose label is its input, beside the release profiles; removing asks in a dialog with Cancel.
                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=releases");
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "New tag", Exact = true }));
                var tag = page.Locator("dialog#tag-editor form").First;
                await tag.Locator("[name=Label]").FillAsync("browser-tag");
                await Steps.SubmitAsync(page, tag);
                var tagField = page.Locator("#tags .tag-card [name=Label]");
                await Assertions.Expect(tagField).ToHaveValueAsync("browser-tag");
                await tagField.FillAsync("renamed-tag");
                await Steps.SaveAsync(page);
                await Assertions.Expect(page.Locator(".tag-card [name=Label]")).ToHaveValueAsync("renamed-tag");
                await Steps.FollowAsync(page, page.Locator(".tag-card").GetByRole(AriaRole.Link, new() { Name = "Remove", Exact = true }));
                await page.Locator("dialog#delete-confirm").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".tag-card [name=Label]")).ToHaveValueAsync("renamed-tag");

                // The quality sizes are sliders: the top of a track is no limit; a thumb moved by keyboard shows its size, stays below the next thumb and is saved by the pill.
                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=quality");
                var size = page.Locator("#quality-movie .size-range", new() { Has = page.Locator(".size-range__name", new() { HasTextString = "WEBDL-1080p" }) });
                await Assertions.Expect(size.Locator("[data-size-output=maximum]")).ToHaveTextAsync("No limit");
                var minimum = size.Locator("[data-size-thumb=minimum]");
                await minimum.FocusAsync();
                await page.Keyboard.PressAsync("End");
                Assert.AreEqual(await size.Locator("[data-size-thumb=preferred]").InputValueAsync(), await minimum.InputValueAsync(), "The minimum stays below the preferred size.");
                await page.Keyboard.PressAsync("Home");
                for (var step = 0; step < 5; step++) await page.Keyboard.PressAsync("PageUp");
                var moved = await minimum.InputValueAsync();
                Assert.AreNotEqual("0", moved);
                await Assertions.Expect(size.Locator("[data-size-output=minimum]")).Not.ToHaveTextAsync("0");
                await Assertions.Expect(page.Locator("[data-save-pill] .save-pill__count")).ToHaveTextAsync("1 change");
                await Steps.SaveAsync(page);
                await page.ReloadAsync();
                Assert.AreEqual(moved, await page.Locator("#quality-movie .size-range", new() { Has = page.Locator(".size-range__name", new() { HasTextString = "WEBDL-1080p" }) })
                    .Locator("[data-size-thumb=minimum]").InputValueAsync());
                await Assertions.Expect(page.Locator("#quality-book .size-range [data-size-thumb=preferred]")).ToHaveCountAsync(0);

                var folder = Directory.CreateDirectory(Path.Combine(JularrInstance.Repository, "TestResults", "e2e")).FullName;
                foreach (var (width, height, device) in new[] { (1440, 1000, "desktop"), (390, 844, "mobile") })
                {
                    await page.SetViewportSizeAsync(width, height);
                    foreach (var tab in new[] { "profiles", "formats", "quality" })
                    {
                        await page.GotoAsync("/Admin/Acquisition/Profiles?tab=" + tab);
                        Assert.IsTrue(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 1"), "Horizontal page overflow in " + tab + " (" + device + ")");
                        await Assertions.Expect(page.Locator(".navigation-tabs")).ToHaveCountAsync(1);
                        await page.ScreenshotAsync(new() { Path = Path.Combine(folder, "arr-" + tab + "-" + device + ".png"), FullPage = true });
                    }
                }

                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray(), string.Join(Environment.NewLine, browser.Problems));
            }
            catch
            {
                await Evidence.SaveAsync(page, nameof(FormatsProfilesAndTags_WorkThroughListsCardsAndDialogs_OnDesktopAndMobile));
                throw;
            }
        }
    }
}
