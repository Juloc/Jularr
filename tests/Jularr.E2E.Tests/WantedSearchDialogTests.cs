using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The manual search as a dialog over the wanted list: every source on its own request, skeletons where a slow source will answer, a source that fails
    // shown as failed, a chip that only filters what is loaded, and the same panel as the page behind the link.
    [TestClass]
    public sealed class WantedSearchDialogTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Dialog_ShowsEachSourceWhenItAnswers_KeepsFailuresVisible_AndFiltersWithoutAskingAgain()
        {
            await using var sources = await Catalogs.StartAsync();
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync($$"""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear")
                VALUES (501, 3, 'Pride and Prejudice', 1813);
                INSERT INTO "WantedItems" ("Id", "WorkId", "NextSearchAt")
                VALUES (501, 501, now() + interval '3 days');
                INSERT INTO "AcquisitionConnections" ("Name", "AcquisitionConnectionTypeId", "BaseUrl")
                VALUES ('Ready catalog', 3, '{{sources.Address}}/ready/'),
                       ('Slow catalog', 3, '{{sources.Address}}/slow/'),
                       ('Broken catalog', 3, '{{sources.Address}}/broken/');
                """);

            try
            {
                await page.SetViewportSizeAsync(1280, 900);
                await page.GotoAsync("/Admin/Wanted");
                await page.GetByRole(AriaRole.Link, new() { Name = "Search manually Pride and Prejudice" }).ClickAsync();
                var dialog = page.Locator("dialog.search-dialog[open]");
                await Assertions.Expect(dialog.GetByRole(AriaRole.Heading, new() { Name = "Pride and Prejudice" })).ToBeVisibleAsync();
                Assert.IsTrue(page.Url.EndsWith("/Admin/Wanted"), "The dialog leaves the page behind it where it is.");

                // The ready source answered; the slow one still shows its two skeleton lines, the broken one says why it gave nothing.
                var ready = dialog.Locator("tbody[data-source][data-state=done]");
                await Assertions.Expect(ready).ToHaveCountAsync(1);
                await Assertions.Expect(ready.GetByRole(AriaRole.Row)).ToHaveCountAsync(2);
                var slow = dialog.Locator("tbody[data-state=loading]");
                await Assertions.Expect(slow).ToHaveCountAsync(1);
                await Assertions.Expect(slow.Locator("tr.search-row--skeleton")).ToHaveCountAsync(2);
                var broken = dialog.GetByRole(AriaRole.Button, new() { Name = "Broken catalog" });
                await Assertions.Expect(broken).ToContainTextAsync("Not reachable");
                await Assertions.Expect(dialog.GetByRole(AriaRole.Status).First).ToContainTextAsync("2 of 3 done");
                await Evidence.SaveAsync(page, "wanted-search-running-desktop");

                // The release the profile allows has its two buttons; the rejected one is grey, with its reason and a warning menu instead.
                var good = ready.GetByRole(AriaRole.Row).Filter(new() { HasText = "Review" });
                await Assertions.Expect(good.GetByRole(AriaRole.Button, new() { Name = "In queue" })).ToBeVisibleAsync();
                await Assertions.Expect(good.GetByRole(AriaRole.Button, new() { Name = "Start now" })).ToBeVisibleAsync();
                var rejected = ready.Locator("tr.search-row--rejected");
                await Assertions.Expect(rejected).ToHaveCountAsync(1);
                await Assertions.Expect(rejected.GetByRole(AriaRole.Button, new() { Name = "In queue" })).ToHaveCountAsync(0);
                await rejected.Locator("summary").ClickAsync();
                await Assertions.Expect(rejected.GetByRole(AriaRole.Button, new() { Name = "Take anyway" })).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "wanted-search-rejected-menu-desktop");
                await rejected.Locator("summary").ClickAsync();

                // The slow source answers; its releases replace the skeletons and the failure stays counted.
                sources.ReleaseSlow.SetResult();
                await Assertions.Expect(dialog.Locator("tbody[data-state=loading]")).ToHaveCountAsync(0);
                await Assertions.Expect(dialog.GetByRole(AriaRole.Status).First).ToContainTextAsync("releases from 2 sources · 1 did not answer");
                await Evidence.SaveAsync(page, "wanted-search-done-desktop");

                // A chip shows only the releases of its source, without a new request.
                var calls = sources.Calls;
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Slow catalog" }).ClickAsync();
                await Assertions.Expect(dialog.Locator("tbody[data-source]:not([hidden])")).ToHaveCountAsync(1);
                await Assertions.Expect(dialog.GetByRole(AriaRole.Status).First).ToContainTextAsync("Slow catalog");
                await Evidence.SaveAsync(page, "wanted-search-filtered-desktop");
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Slow catalog" }).ClickAsync();
                await Assertions.Expect(dialog.Locator("tbody[data-source]:not([hidden])")).ToHaveCountAsync(3);
                Assert.AreEqual(calls, sources.Calls, "Filtering asked the sources again.");

                // On a phone every release is a card and nothing runs over the side.
                await page.SetViewportSizeAsync(390, 844);
                Assert.IsFalse(await dialog.EvaluateAsync<bool>("element => element.scrollWidth > element.clientWidth + 1 || document.body.scrollWidth > innerWidth + 1"));
                await Evidence.SaveAsync(page, "wanted-search-done-phone");
                await page.SetViewportSizeAsync(1280, 900);

                // The dialog closes without leaving the page; its link still goes to the page that shows the same panel.
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(page.Locator("dialog.search-dialog")).ToHaveCountAsync(0);
                Assert.IsTrue(page.Url.EndsWith("/Admin/Wanted"));
                await page.GotoAsync("/Admin/Wanted/501/Search");
                await Assertions.Expect(page.Locator("main [data-search-panel] tbody[data-state=done]")).ToHaveCountAsync(2);
                await Evidence.SaveAsync(page, "wanted-search-page-desktop");
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, nameof(Dialog_ShowsEachSourceWhenItAnswers_KeepsFailuresVisible_AndFiltersWithoutAskingAgain));
                throw;
            }
        }

        private sealed class Catalogs(WebApplication app) : IAsyncDisposable
        {
            private int _calls;

            public string Address => app.Urls.Single().TrimEnd('/');
            public int Calls => Volatile.Read(ref _calls);
            public TaskCompletionSource ReleaseSlow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public static async Task<Catalogs> StartAsync()
            {
                var app = WebApplication.CreateSlimBuilder().Build();
                app.Urls.Add("http://127.0.0.1:0");
                var sources = new Catalogs(app);
                app.MapGet("/ready/books", () =>
                {
                    Interlocked.Increment(ref sources._calls);
                    return Results.Text("""
                        {"results":[{"id":1342,"title":"Pride and Prejudice","authors":[{"name":"Austen, Jane"}],"languages":["en"],"formats":{"application/epub+zip":"https://www.gutenberg.org/ebooks/1342.epub3.images"}},
                        {"id":2701,"title":"Moby Dick","authors":[{"name":"Melville, Herman"}],"languages":["en"],"formats":{"application/epub+zip":"https://www.gutenberg.org/ebooks/2701.epub3.images"}}]}
                        """, "application/json");
                });
                app.MapGet("/slow/books", async (HttpContext http) =>
                {
                    Interlocked.Increment(ref sources._calls);
                    await sources.ReleaseSlow.Task.WaitAsync(http.RequestAborted);
                    return Results.Text("""
                        {"results":[{"id":1343,"title":"Pride and Prejudice","authors":[{"name":"Austen, Jane"}],"languages":["en"],"formats":{"application/epub+zip":"https://www.gutenberg.org/ebooks/1343.epub3.images"}}]}
                        """, "application/json");
                });
                app.MapGet("/broken/books", () =>
                {
                    Interlocked.Increment(ref sources._calls);
                    return Results.StatusCode(500);
                });
                await app.StartAsync();
                return sources;
            }

            public async ValueTask DisposeAsync() => await app.DisposeAsync();
        }
    }
}
