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

                // One row of navigation joins every page of the acquisition settings, and no page offers a waiting time.
                foreach (var label in new[] { "Quality profiles", "Custom formats", "Quality sizes", "Release profiles", "Tags", "Indexers" })
                {
                    await page.GotoAsync("/Admin/Acquisition/Profiles");
                    await page.Locator(".navigation-tabs").GetByRole(AriaRole.Link, new() { Name = label, Exact = true }).ClickAsync();
                    await Assertions.Expect(page.Locator(".navigation-tabs")).ToHaveCountAsync(1);
                    await Assertions.Expect(page.Locator("main [role=alert]")).ToHaveCountAsync(0);
                    var text = (await page.Locator("main").InnerTextAsync()).ToLowerInvariant();
                    Assert.IsFalse(text.Contains("delay", StringComparison.Ordinal) || text.Contains("wait", StringComparison.Ordinal), "A waiting time is offered on " + label);
                }

                // A custom format with its conditions is a dialog of the list.
                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=formats&mediaTypeId=1");
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

                // The profile editor scores every format of the media type; a score of the new format is kept.
                await page.GotoAsync("/Admin/Acquisition/Profiles/New?type=Movie");
                var profile = page.Locator("main form", new() { Has = page.Locator("[name=MinimumFormatScore]") });
                await profile.Locator("[name=Name]").FillAsync("Browser quality");
                await profile.Locator(".ui-form__row", new() { HasText = "Browser format" }).Locator("input[name$='.Score']").FillAsync("100");
                await profile.Locator("[name=MinimumFormatScore]").FillAsync("100");
                await Steps.SaveAsync(page);
                await Assertions.Expect(page.Locator("main [role=alert]")).ToHaveCountAsync(0);
                await Assertions.Expect(profile.Locator(".ui-form__row", new() { HasText = "Browser format" }).Locator("input[name$='.Score']")).ToHaveValueAsync("100");
                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=profiles&mediaTypeId=1");
                await Assertions.Expect(Row(page, "Browser quality")).ToBeVisibleAsync();

                // A release profile is added in a dialog with Cancel; afterwards its settings are inputs of its card, saved by the page's pill.
                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=release&mediaTypeId=1");
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "New release profile", Exact = true }));
                Assert.AreEqual(1, await page.Locator("dialog#release-editor").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).CountAsync());
                var release = page.Locator("dialog#release-editor form").First;
                await release.Locator("[name=Name]").FillAsync("Browser release");
                await release.Locator("[name=Required]").FillAsync("/^TEAM{1,3},HDR$/i");
                await release.Locator("[name=Ignored]").FillAsync("x265\nCAM");
                await Steps.SubmitAsync(page, release);
                await Assertions.Expect(page.Locator("dialog#release-editor[open]")).ToHaveCountAsync(0);
                Assert.AreEqual(0, await page.GetByRole(AriaRole.Link, new() { Name = "Edit", Exact = true }).CountAsync(), "Release profiles have no Edit button.");
                var releaseCard = Card(page, "Browser release");
                Assert.AreEqual("/^TEAM{1,3},HDR$/i", await releaseCard.Locator("[name=Required]").InputValueAsync());
                Assert.AreEqual("x265\nCAM", await releaseCard.Locator("[name=Ignored]").InputValueAsync());
                await Assertions.Expect(page.Locator("[data-save-pill]")).ToBeHiddenAsync();
                await releaseCard.Locator("[name=Ignored]").FillAsync("x265");
                await Assertions.Expect(page.Locator("[data-save-pill] .save-pill__count")).ToHaveTextAsync("1 change");
                await Steps.SaveAsync(page);
                await page.ReloadAsync();
                Assert.AreEqual("x265", await Card(page, "Browser release").Locator("[name=Ignored]").InputValueAsync());

                // A tag is a card whose label is its input; removing asks in a dialog with Cancel.
                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=tags");
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "New tag", Exact = true }));
                var tag = page.Locator("dialog#tag-editor form").First;
                await tag.Locator("[name=Label]").FillAsync("browser-tag");
                await Steps.SubmitAsync(page, tag);
                var tagField = page.Locator(".tag-card [name=Label]");
                await Assertions.Expect(tagField).ToHaveValueAsync("browser-tag");
                await tagField.FillAsync("renamed-tag");
                await Steps.SaveAsync(page);
                await Assertions.Expect(page.Locator(".tag-card [name=Label]")).ToHaveValueAsync("renamed-tag");
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Remove", Exact = true }));
                await page.Locator("dialog#delete-confirm").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".tag-card [name=Label]")).ToHaveValueAsync("renamed-tag");

                await page.SetViewportSizeAsync(1440, 1000);
                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=formats&mediaTypeId=1");
                var folder = Directory.CreateDirectory(Path.Combine(JularrInstance.Repository, "TestResults", "e2e")).FullName;
                await page.ScreenshotAsync(new() { Path = Path.Combine(folder, "arr-desktop.png"), FullPage = true });
                await page.SetViewportSizeAsync(390, 844);
                foreach (var tab in new[] { "profiles", "formats", "sizes", "release", "tags" })
                {
                    await page.GotoAsync("/Admin/Acquisition/Profiles?tab=" + tab + "&mediaTypeId=1");
                    Assert.IsTrue(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 1"), "Horizontal page overflow in " + tab);
                    await Assertions.Expect(page.Locator(".navigation-tabs")).ToHaveCountAsync(1);
                }

                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=formats&mediaTypeId=1");
                await page.ScreenshotAsync(new() { Path = Path.Combine(folder, "arr-mobile.png"), FullPage = true });
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
