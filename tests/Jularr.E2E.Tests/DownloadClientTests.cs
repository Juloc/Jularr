using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class DownloadClientTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Sabnzbd_AllTabsAndConfirmedControls_UseProviderStateAndKeepUnassignedFiles()
        {
            using var folders = new Folders("Movies");
            var file = Path.Combine(folders.Fixtures, "pending.txt");
            await File.WriteAllTextAsync(file, "Pending provider download");
            var release = new FakeRelease("Queued.Console.Movie.2026.1080p", "console-job", 2040, file);
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            fakes.HoldDownloads = true;
            fakes.Offer(release);
            using (var client = new HttpClient())
            using (var queued = await client.PostAsync(new Uri(fakes.Address, "sabnzbd/api"), new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["apikey"] = FakeServices.ClientKey,
                ["mode"] = "addurl",
                ["name"] = fakes.NzbAddress(release).ToString()
            })))
            {
                queued.EnsureSuccessStatusCode();
            }

            var leftover = Path.Combine(folders.Downloads, "unassigned-console.nfo");
            await File.WriteAllTextAsync(leftover, "Keep this unassigned file");
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.AddUsenetAsync(page, fakes, "2000,5000");
                var tabs = page.Locator(".navigation-tabs");
                foreach (var tab in new[] { "Downloads", "Statistics", "History", "Files", "General" })
                {
                    await tabs.GetByRole(AriaRole.Link, new() { Name = tab, Exact = true }).ClickAsync();
                    await Assertions.Expect(tabs.GetByRole(AriaRole.Link, new() { Name = tab, Exact = true })).ToHaveAttributeAsync("aria-current", "page");
                    if (tab == "Files")
                    {
                        await Assertions.Expect(page.GetByText("unassigned-console.nfo", new() { Exact = true })).ToBeVisibleAsync();
                    }
                }

                await tabs.GetByRole(AriaRole.Link, new() { Name = "Downloads", Exact = true }).ClickAsync();
                await Assertions.Expect(page.GetByText(release.Title, new() { Exact = true })).ToBeVisibleAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Pause", Exact = true }).ClickAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Resume", Exact = true })).ToBeVisibleAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Resume", Exact = true }).ClickAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Pause", Exact = true })).ToBeVisibleAsync();
                await page.GetByRole(AriaRole.Link, new() { Name = "Cancel download", Exact = true }).ClickAsync();
                await Assertions.Expect(page.GetByText("Cancel this download and remove its temporary files?", new() { Exact = true })).ToBeVisibleAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Confirm", Exact = true }).ClickAsync();
                await Assertions.Expect(page.GetByText(release.Title, new() { Exact = true })).ToHaveCountAsync(0);
                Assert.IsTrue(File.Exists(leftover));
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "download-client-console");
                throw;
            }
        }
    }
}
