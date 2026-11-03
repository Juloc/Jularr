using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class ManualBookSearchTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task SlowBookSource_DoesNotLoseOtherResults_AndLeavingTheSearchCancelsIt()
        {
            await using var sources = await BookSources.StartAsync();
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
                       ('Slow catalog', 3, '{{sources.Address}}/slow/');
                """);

            // The page shows the same panel; the ready source answers at once, the slow one gives up on its own and is said to have no answer, never "no releases".
            await page.GotoAsync("/Admin/Wanted/501/Search");
            var document = await page.QuerySelectorAsync("html");
            var result = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Ready catalog" });
            await Assertions.Expect(result).ToContainTextAsync("Pride and Prejudice", new() { Timeout = 45_000 });
            await sources.FirstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(45));
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Slow catalog" })).ToContainTextAsync("No answer in time", new() { Timeout = 10_000 });
            Assert.IsTrue(await document!.EvaluateAsync<bool>("element => element.isConnected"), "The search must not replace the page.");
            await Assertions.Expect(page.Locator("main [role=alert]")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("main [data-search-empty]")).ToBeHiddenAsync();
            await Evidence.SaveAsync(page, "manual-book-search-slow-source");

            // Searching again asks the sources again; leaving the page cancels what is still running.
            await page.GetByRole(AriaRole.Button, new() { Name = "Search again" }).ClickAsync();
            await sources.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await page.GotoAsync("/Admin/Wanted/501");
            await sources.SecondCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Search manually", Exact = true })).ToBeVisibleAsync();
            CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
        }

        private sealed class BookSources(WebApplication app) : IAsyncDisposable
        {
            public string Address => app.Urls.Single().TrimEnd('/');
            public TaskCompletionSource FirstCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource SecondCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _slowCalls;

            public static async Task<BookSources> StartAsync()
            {
                var app = WebApplication.CreateSlimBuilder().Build();
                app.Urls.Add("http://127.0.0.1:0");
                var sources = new BookSources(app);
                app.MapGet("/ready/books", () => Results.Text("""
                    {"results":[{"id":1342,"title":"Pride and Prejudice","authors":[{"name":"Austen, Jane"}],"languages":["en"],"formats":{"application/epub+zip":"https://www.gutenberg.org/ebooks/1342.epub3.images"}}]}
                    """, "application/json"));
                app.MapGet("/slow/books", async (HttpContext http) =>
                {
                    var call = Interlocked.Increment(ref sources._slowCalls);
                    if (call == 2) sources.SecondStarted.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, http.RequestAborted); }
                    finally
                    {
                        if (call == 1) sources.FirstCancelled.TrySetResult();
                        if (call == 2) sources.SecondCancelled.TrySetResult();
                    }
                });
                await app.StartAsync();
                return sources;
            }

            public async ValueTask DisposeAsync() => await app.DisposeAsync();
        }
    }
}
