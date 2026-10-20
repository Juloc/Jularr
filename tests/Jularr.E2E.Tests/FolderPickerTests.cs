using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class FolderPickerTests
    {
        [TestMethod]
        [DataRow("/Admin/Storage", 1440, 900)]
        [DataRow("/Setup/Storage", 390, 844)]
        [Timeout(180_000)]
        public async Task FolderDialog_PreservesDraftAndSelectsWithoutPageNavigation(string address, int width, int height)
        {
            using var folders = new Folders("Media", "Other", "Many");
            Directory.CreateDirectory(Path.Combine(folders.Library, "Media", "Comics & Bücher"));
            for (var index = 0; index <= 500; index++)
            {
                Directory.CreateDirectory(Path.Combine(folders.Library, "Many", "Folder" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)));
            }
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await page.SetViewportSizeAsync(width, height);
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await page.GotoAsync(address);
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Add local path", Exact = true }));
                var mount = page.Locator("form").Filter(new() { Has = page.Locator("[name=SharePath]") });
                await mount.Locator("[name=Name]").FillAsync("Library");
                await mount.Locator("[name=SharePath]").SelectOptionAsync(folders.Library);
                await mount.Locator("[name=IsReadOnly][value=False]").CheckAsync();
                await Steps.SubmitAsync(page, mount);

                var library = Steps.FormWith(page, "Add library path");
                await library.Locator("[name=Name]").FillAsync("Unsaved library name");
                await library.Locator("[name=MediaTypeId]").SelectOptionAsync(new SelectOptionValue { Label = "Book" });
                var originalAddress = page.Url;
                var navigations = 0;
                page.FrameNavigated += (_, frame) => { if (frame == page.MainFrame) navigations++; };
                await library.GetByRole(AriaRole.Link, new() { Name = "Browse folders", Exact = true }).ClickAsync();
                var picker = page.GetByRole(AriaRole.Dialog, new() { Name = "Browse folders", Exact = true });
                await Assertions.Expect(picker).ToBeVisibleAsync();
                await picker.GetByRole(AriaRole.Link, new() { Name = "Media", Exact = true }).ClickAsync();
                await Assertions.Expect(picker.GetByRole(AriaRole.Link, new() { Name = "Comics & Bücher", Exact = true })).ToBeVisibleAsync();
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(picker).Not.ToBeVisibleAsync();
                await Assertions.Expect(library.Locator("[name=RelativePath]")).ToHaveValueAsync("");
                await Assertions.Expect(library.Locator("[name=Name]")).ToHaveValueAsync("Unsaved library name");
                await Assertions.Expect(library.Locator("[name=MediaTypeId]")).ToHaveValueAsync("3");

                await library.GetByRole(AriaRole.Link, new() { Name = "Browse folders", Exact = true }).ClickAsync();
                await picker.GetByRole(AriaRole.Link, new() { Name = "Connection root", Exact = true }).ClickAsync();
                await picker.GetByRole(AriaRole.Link, new() { Name = "Many", Exact = true }).ClickAsync();
                await Assertions.Expect(picker.GetByRole(AriaRole.Link, new() { Name = "Folder499", Exact = true })).ToBeAttachedAsync();
                await Assertions.Expect(picker.GetByRole(AriaRole.Link, new() { Name = "Folder500", Exact = true })).ToHaveCountAsync(0);
                await picker.GetByRole(AriaRole.Link, new() { Name = "More folders", Exact = true }).ClickAsync();
                await Assertions.Expect(picker.GetByRole(AriaRole.Link, new() { Name = "Folder500", Exact = true })).ToBeVisibleAsync();
                await picker.GetByRole(AriaRole.Link, new() { Name = "Connection root", Exact = true }).ClickAsync();
                await picker.GetByRole(AriaRole.Link, new() { Name = "Media", Exact = true }).ClickAsync();
                await picker.GetByRole(AriaRole.Link, new() { Name = "Comics & Bücher", Exact = true }).ClickAsync();
                await Assertions.Expect(picker.GetByText("This folder has no subfolders.", new() { Exact = true })).ToBeVisibleAsync();
                await Assertions.Expect(picker.GetByRole(AriaRole.Button, new() { Name = "Use this folder", Exact = true })).ToBeFocusedAsync();
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"));
                await page.ScreenshotAsync(new() { Path = Path.Combine(JularrInstance.Repository, "TestResults", "e2e", "folder-dialog-" + width + ".png") });
                await picker.GetByRole(AriaRole.Button, new() { Name = "Use this folder", Exact = true }).ClickAsync();
                await Assertions.Expect(picker).Not.ToBeVisibleAsync();
                await Assertions.Expect(library.Locator("[name=RelativePath]")).ToHaveValueAsync("Media/Comics & Bücher");
                await Assertions.Expect(library.Locator("[name=Name]")).ToHaveValueAsync("Unsaved library name");
                Assert.AreEqual(originalAddress, page.Url);
                Assert.AreEqual(0, navigations);
                await Steps.SubmitAsync(page, library);
                await Assertions.Expect(page.GetByRole(AriaRole.Cell, new() { Name = "/Media/Comics & Bücher", Exact = true })).ToBeVisibleAsync();
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "folder-picker-" + width);
                throw;
            }
        }
    }
}
