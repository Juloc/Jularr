using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class ArrSettingsTests
    {
        [TestMethod]
        [Timeout(600_000)]
        public async Task FormatsProfilesAndTags_WorkThroughNativeForms_OnDesktopAndMobile()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                foreach (var label in new[] { "Quality profiles", "Custom formats", "Quality sizes", "Release profiles", "Tags" })
                {
                    await page.GotoAsync("/Admin/Acquisition/Profiles");
                    await page.Locator(".navigation-tabs").GetByRole(AriaRole.Link, new() { Name = label, Exact = true }).ClickAsync();
                    await Assertions.Expect(page.Locator(".navigation-tabs")).ToHaveCountAsync(1);
                    await Assertions.Expect(page.Locator("main [role=alert]")).ToHaveCountAsync(0);
                }

                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=formats&mediaTypeId=1");
                var format = page.Locator("main form", new() { Has = page.Locator("[name=IncludeWhenRenaming]") });
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
                await Steps.SaveAsync(page);
                await Assertions.Expect(page.Locator("main [role=alert]")).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator("tr", new() { HasText = "Browser format" })).ToBeVisibleAsync();

                await page.GotoAsync("/Admin/Acquisition/Profiles/New?type=Movie");
                var profile = page.Locator("main form", new() { Has = page.Locator("[name=MinimumFormatScore]") });
                await profile.Locator("[name=Name]").FillAsync("Browser quality");
                await profile.Locator("tr", new() { HasText = "Browser format" }).GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).ClickAsync();
                Assert.AreEqual("Browser quality", await profile.Locator("[name=Name]").InputValueAsync());
                await profile.Locator("[name='FormatScores[0].Score']").FillAsync("100");
                await profile.Locator("[name=MinimumFormatScore]").FillAsync("100");
                await Steps.SaveAsync(page);
                await Assertions.Expect(page.Locator("main [role=alert]")).ToHaveCountAsync(0);
                Assert.AreEqual("100", await profile.Locator("[name='FormatScores[0].Score']").InputValueAsync());

                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=release&mediaTypeId=1");
                var release = page.Locator("main form", new() { Has = page.Locator("[name=Required]") });
                await release.Locator("[name=Name]").FillAsync("Browser release");
                await release.Locator("[name=Required]").FillAsync("/^TEAM{1,3},HDR$/i");
                await release.Locator("[name=Ignored]").FillAsync("x265\nCAM");
                await Steps.SaveAsync(page);
                Assert.AreEqual("/^TEAM{1,3},HDR$/i", await release.Locator("[name=Required]").InputValueAsync());
                Assert.AreEqual("x265\nCAM", await release.Locator("[name=Ignored]").InputValueAsync());

                await page.GotoAsync("/Admin/Acquisition/Profiles?tab=tags");
                var tag = page.Locator("main form", new() { Has = page.Locator("[name=Label]") });
                await tag.Locator("[name=Label]").FillAsync("browser-tag");
                await Steps.SaveAsync(page);
                await Assertions.Expect(page.Locator("tr", new() { HasText = "browser-tag" })).ToBeVisibleAsync();
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
                await using var native = await browser.Context.Browser!.NewContextAsync(new()
                {
                    BaseURL = jularr.Address.ToString(), JavaScriptEnabled = false, StorageState = await browser.Context.StorageStateAsync()
                });
                await Steps.KeepLocalAsync(native);
                var nativePage = await native.NewPageAsync();
                await nativePage.GotoAsync("/Admin/Acquisition/Profiles?tab=formats&mediaTypeId=1");
                var nativeForm = nativePage.Locator("main form", new() { Has = nativePage.Locator("[name=IncludeWhenRenaming]") });
                await nativeForm.Locator("[name=Name]").FillAsync("Native draft");
                await nativeForm.Locator("[name='Conditions[0].Kind']").SelectOptionAsync("8");
                await nativeForm.GetByRole(AriaRole.Button, new() { Name = "Apply condition type", Exact = true }).ClickAsync();
                await Assertions.Expect(nativeForm.Locator("[name='Conditions[0].Minimum']")).ToBeVisibleAsync();
                Assert.AreEqual("Native draft", await nativeForm.Locator("[name=Name]").InputValueAsync());
                await Assertions.Expect(nativePage.Locator("tr", new() { HasText = "Native draft" })).ToHaveCountAsync(0);
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray(), string.Join(Environment.NewLine, browser.Problems));
            }
            catch
            {
                await Evidence.SaveAsync(page, nameof(FormatsProfilesAndTags_WorkThroughNativeForms_OnDesktopAndMobile));
                throw;
            }
        }
    }
}
