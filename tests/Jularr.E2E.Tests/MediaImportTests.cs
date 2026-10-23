using System.Text;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The Import page: a small mixed list given as a file through the file input, previewed, imported and repeated without a second copy of anything;
    // and a list longer than one run that carries the rows' own choices over to the rest.
    [TestClass]
    public sealed class MediaImportTests
    {
        private const string AlbumId = "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b";

        private const string List = """
            {"version":1,"items":[
              {"type":"movie","ids":{"tmdb":"10378"},"title":"Big Buck Bunny","year":2008},
              {"type":"series","ids":{"tmdb":"99001"},"title":"Jularr Test Series"},
              {"type":"book","title":"Pride and Prejudice","author":"Jane Austen"},
              {"type":"music","ids":{"musicbrainz":"0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b"},"title":"Example Album","artist":"Example Artist"},
              {"type":"movie","ids":{"tmdb":"10378"},"title":"Bunny again"},
              {"type":"movie","bogus":1,"title":"Broken entry"}
            ]}
            """;

        // A switch is flipped through its visible track, as a person does.
        private static Task FlipAsync(ILocator input) => input.Locator("xpath=ancestor::label[1]").ClickAsync();

        private static FilePayload Payload(string name, string text, string type = "application/json") => new() { Name = name, MimeType = type, Buffer = Encoding.UTF8.GetBytes(text) };

        [TestMethod]
        [Timeout(300_000)]
        public async Task MixedList_IsUploadedPreviewedImportedAndRepeatedWithoutDuplicates()
        {
            using var folders = new Folders("Movies");
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:OpenLibrary"] = fakes.OpenLibrary.ToString(),
                ["Providers:MusicBrainz"] = fakes.MusicBrainz.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);

                // The page is one of the Media entry's pages, reached by its row of chips; the file input shows its drop zone, the field behind it stays out of sight.
                await page.GotoAsync("/Admin/Import");
                await Assertions.Expect(page.Locator(".navigation-tabs a[href='/Admin/Import'][aria-current=page]")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator("[name=Json]")).ToBeVisibleAsync();
                var input = page.Locator(".file-input[data-file-ready]");
                await Assertions.Expect(input.Locator(".file-input__zone")).ToBeVisibleAsync();
                await Assertions.Expect(input.GetByRole(AriaRole.Button, new() { Name = "Choose file" })).ToBeVisibleAsync();
                await Assertions.Expect(input.Locator(".file-input__limit")).ToHaveTextAsync("JSON · up to 1.0 MB");
                await Assertions.Expect(page.Locator('.media-import__upload select[name="Action"]')).ToHaveValueAsync("1");
                // This test deliberately opts in to monitor/search instead of relying on a dangerous default.
                await page.Locator('.media-import__upload select[name="Action"]').SelectOptionAsync("3");
                await Assertions.Expect(input.Locator("input[type=file]")).ToHaveCSSAsync("opacity", "0");
                await Assertions.Expect(page.Locator("[data-import-status]")).ToHaveTextAsync("Waiting for a list");
                await Evidence.SaveAsync(page, "media-import-upload");

                // The example is a download and a sample that opens, with its copy button.
                var example = page.GetByRole(AriaRole.Link, new() { Name = "Example file" });
                await Assertions.Expect(example).ToHaveAttributeAsync("download", "jularr-media-list.json");
                await page.Locator(".code-sample summary").ClickAsync();
                await Assertions.Expect(page.Locator(".code-sample [data-code-text]")).ToContainTextAsync("\"version\": 1");
                await page.Locator("[data-code-copy]").ClickAsync();
                await Assertions.Expect(page.Locator("[data-code-copy]")).ToHaveAttributeAsync("data-state", "copied");
                await Evidence.SaveAsync(page, "media-import-sample");

                // A file of the wrong kind or beyond the limit is refused with its reason; nothing stays in the field.
                var file = input.Locator("input[type=file]");
                await file.SetInputFilesAsync(Payload("list.txt", "{}", "text/plain"));
                await Assertions.Expect(input.Locator("[data-file-error]")).ToHaveTextAsync("Only JSON files are accepted.");
                await file.SetInputFilesAsync(Payload("big.json", new string(' ', 1_048_577)));
                await Assertions.Expect(input.Locator("[data-file-error]")).ToHaveTextAsync("The file is larger than 1.0 MB.");
                Assert.AreEqual(0, await file.EvaluateAsync<int>("field => field.files.length"));
                await Assertions.Expect(input.Locator("[data-file-chosen]")).ToBeHiddenAsync();

                // The list is chosen: its name and size appear and the page reads it; removing it brings the page back.
                await file.SetInputFilesAsync(Payload("list.json", List));
                await Assertions.Expect(input.Locator("[data-file-name]")).ToHaveTextAsync("list.json");
                await Assertions.Expect(input.Locator("[data-file-size]")).ToHaveTextAsync(Encoding.UTF8.GetByteCount(List).ToString(System.Globalization.CultureInfo.InvariantCulture) + " B");
                await Assertions.Expect(input.Locator("[data-file-error]")).ToBeHiddenAsync();
                await Assertions.Expect(page.Locator("[data-import-status]")).ToHaveTextAsync("Valid list · 6 entries");
                await Evidence.SaveAsync(page, "media-import-file");
                await input.GetByRole(AriaRole.Button, new() { Name = "Remove file" }).ClickAsync();
                await Assertions.Expect(input.Locator("[data-file-chosen]")).ToBeHiddenAsync();
                await Assertions.Expect(page.Locator("[data-import-status]")).ToHaveTextAsync("Waiting for a list");
                await page.Locator("[name=Json]").FillAsync("{\"version\":1,\"items\":[");
                await Assertions.Expect(page.Locator("[data-import-status]")).ToHaveTextAsync("Not a valid list");
                await page.Locator("[name=Json]").FillAsync("");
                await file.SetInputFilesAsync(Payload("list.json", List));
                await Assertions.Expect(input.Locator("[data-file-name]")).ToHaveTextAsync("list.json");

                // The list is previewed: three new, one open with a proposed book, a duplicate and an invalid entry.
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Preview import"));
                var summary = page.Locator(".media-import__summary");
                foreach (var (number, status) in new[] { ("3", "New"), ("1", "Open"), ("1", "Duplicate"), ("1", "Invalid") })
                {
                    await Assertions.Expect(summary.Locator(".tag").Filter(new() { HasText = status })).ToContainTextAsync(number);
                }

                var rows = page.Locator("main table tbody tr");
                await Assertions.Expect(rows).ToHaveCountAsync(6);
                await Assertions.Expect(rows.Nth(5)).ToContainTextAsync("Unknown key: bogus");
                await Assertions.Expect(rows.Nth(4)).ToContainTextAsync("Same as row 1");
                await Assertions.Expect(rows.Nth(0).Locator(".state-chip svg")).ToHaveCountAsync(1);
                var book = rows.Nth(2);
                await Assertions.Expect(book.Locator("select[name='Choices[2].Choice']")).ToHaveValueAsync("openlibrary:OL66554W");
                await book.GetByRole(AriaRole.Button, new() { Name = "Compare editions" }).ClickAsync();
                await Assertions.Expect(book.Locator("[data-import-edition-details]")).ToContainTextAsync("9780141439518");
                await Assertions.Expect(book.Locator("[data-import-edition-details]")).ToContainTextAsync("Paperback");
                await Assertions.Expect(book.Locator("input[type=checkbox][name='Choices[2].Include']")).Not.ToBeCheckedAsync();
                await Assertions.Expect(rows.Nth(0).Locator("input[type=checkbox][name='Choices[0].Include']")).ToBeCheckedAsync();
                await Assertions.Expect(rows.Nth(0).Locator("select[name='Choices[0].Action']")).ToHaveValueAsync("");
                Assert.AreEqual(0, await page.Locator("main table tbody tr a[href^='/Admin/Works/']").CountAsync(), "The preview must not add anything.");

                // Select all follows the rows: three of four chosen, then all, then none (and nothing to import).
                var all = page.Locator("[data-import-all]");
                var count = page.Locator("[data-import-count]");
                await Assertions.Expect(count).ToHaveTextAsync("3 of 4 selected");
                await Assertions.Expect(all).Not.ToBeCheckedAsync();
                await Evidence.SaveAsync(page, "media-import-preview");
                await FlipAsync(all);
                await Assertions.Expect(count).ToHaveTextAsync("4 of 4 selected");
                await Assertions.Expect(book.Locator("input[type=checkbox][name='Choices[2].Include']")).ToBeCheckedAsync();
                await FlipAsync(all);
                await Assertions.Expect(count).ToHaveTextAsync("0 of 4 selected");
                await Assertions.Expect(page.Locator("[data-import-apply]")).ToBeDisabledAsync();
                await FlipAsync(book.Locator("input[name='Choices[2].Include']"));
                await Assertions.Expect(count).ToHaveTextAsync("1 of 4 selected");
                await Assertions.Expect(all).Not.ToBeCheckedAsync();
                await FlipAsync(rows.Nth(0).Locator("input[name='Choices[0].Include']"));
                await FlipAsync(rows.Nth(1).Locator("input[name='Choices[1].Include']"));
                await FlipAsync(rows.Nth(3).Locator("input[name='Choices[3].Include']"));
                await Assertions.Expect(all).ToBeCheckedAsync();

                // A delayed provider response must not permit an import against stale matches.
                var releaseSearch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var streamedSearch = page.WaitForRequestAsync(request => request.Url.Contains("handler=PreviewStream", StringComparison.Ordinal));
                await page.RouteAsync("**/Admin/Import?handler=PreviewStream", async route =>
                {
                    await releaseSearch.Task;
                    await route.ContinueAsync();
                });
                try
                {
                    await page.GetByRole(AriaRole.Button, new() { Name = "Update preview" }).ClickAsync(new() { NoWaitAfter = true });
                    Assert.AreEqual("POST", (await streamedSearch).Method);
                    await Assertions.Expect(page.Locator("[data-import-apply]")).ToBeDisabledAsync();
                    await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Update preview" })).ToBeDisabledAsync();
                }
                finally
                {
                    releaseSearch.TrySetResult(true);
                }
                await Assertions.Expect(page.Locator("[data-import-count]")).ToHaveTextAsync("4 of 4 selected");
                await Assertions.Expect(page.Locator("[data-import-intent]")).ToContainTextAsync("4 selected · 4 to monitor · 4 to search");
                await Assertions.Expect(page.Locator("[data-import-apply]")).ToBeEnabledAsync();
                await page.UnrouteAsync("**/Admin/Import?handler=PreviewStream");

                // A narrow screen keeps the page in its width.
                await page.SetViewportSizeAsync(390, 844);
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"));
                await Evidence.SaveAsync(page, "media-import-preview-mobile");
                await page.SetViewportSizeAsync(1280, 720);

                // The person imports the chosen rows without leaving the page; actual per-row outcomes stream live.
                await page.Locator("[data-import-apply]").ClickAsync();
                await Assertions.Expect(page.Locator(".media-import__activity--done")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator(".media-import__activity-list li", new() { HasText = "Added, monitored" })).ToHaveCountAsync(4);
                await Assertions.Expect(page.Locator(".media-import__activity progress")).ToHaveJSPropertyAsync("value", 6);
                await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Download import report" })).ToHaveCountAsync(1);
                await Evidence.SaveAsync(page, "media-import-result");
                await page.SetViewportSizeAsync(390, 844);
                await Evidence.SaveAsync(page, "media-import-result-mobile");
                await page.SetViewportSizeAsync(1280, 720);

                // The titles are in the library once, each with a target.
                await page.GotoAsync("/Admin/Works");
                foreach (var title in new[] { FakeServices.MovieTitle, FakeServices.SeriesTitle, FakeServices.BookTitle, FakeServices.AlbumTitle })
                {
                    await Assertions.Expect(page.Locator("main .list-row", new() { HasText = title })).ToHaveCountAsync(1);
                }

                await page.GotoAsync("/Admin/Wanted");
                foreach (var title in new[] { FakeServices.MovieTitle, FakeServices.SeriesTitle, FakeServices.BookTitle, FakeServices.AlbumTitle })
                {
                    await Assertions.Expect(page.Locator("main .list-row", new() { HasText = title })).ToHaveCountAsync(1);
                }

                // The same list again, pasted, finds everything in the library; importing it changes nothing.
                await page.GotoAsync("/Admin/Import");
                await page.Locator("[name=Json]").FillAsync(List.Replace("{\"type\":\"book\",\"title\":\"Pride and Prejudice\",\"author\":\"Jane Austen\"}",
                    "{\"type\":\"book\",\"ids\":{\"openlibrary\":\"OL66554W\"}}", StringComparison.Ordinal));
                await Assertions.Expect(page.Locator("[data-import-status]")).ToHaveTextAsync("Valid list · 6 entries");
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Preview import"));
                await Assertions.Expect(page.Locator(".media-import__summary .tag").Filter(new() { HasText = "In library" })).ToContainTextAsync("4");
                await Assertions.Expect(page.Locator("main table tbody tr input[type=checkbox]")).ToHaveCountAsync(0);
                // All rows already exist; no need to submit again to discover there are no changes.
                await Assertions.Expect(page.Locator("[data-import-count]")).ToHaveCountAsync(0);
                await page.GotoAsync("/Admin/Works");
                await Assertions.Expect(page.Locator("main .list-row")).ToHaveCountAsync(4);
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-media-import");
                throw;
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task LongList_ContinuesWithTheChoicesOfItsRows()
        {
            using var folders = new Folders("Movies");
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);

                // Forty-two new films: a run takes forty and leaves two for the next.
                var list = "{\"version\":1,\"items\":[" + string.Join(',', Enumerable.Range(30000, 42).Select(id => "{\"type\":\"movie\",\"ids\":{\"tmdb\":\"" + id + "\"}}")) + "]}";
                await page.GotoAsync("/Admin/Import");
                await page.Locator("input[type=file]").SetInputFilesAsync(Payload("bulk.json", list));
                await page.Locator('.media-import__upload select[name="Action"]').SelectOptionAsync("3");
                await Assertions.Expect(page.Locator("[data-import-status]")).ToHaveTextAsync("Valid list · 42 entries");
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Preview import"));
                var rows = page.Locator("main table tbody tr");
                await Assertions.Expect(rows).ToHaveCountAsync(42);
                await Assertions.Expect(page.Locator("[data-import-count]")).ToHaveTextAsync("42 of 42 selected");

                // The last two rows differ from the list: the next one is only added, the last one is monitored without a search.
                await page.Locator("select[name='Choices[40].Action']").SelectOptionAsync(new SelectOptionValue { Label = "Add only" });
                await page.Locator("select[name='Choices[41].Action']").SelectOptionAsync(new SelectOptionValue { Label = "Monitor" });
                await page.Locator("[data-import-apply]").ClickAsync();
                // Forty provider imports are bounded per server pass, but the browser
                // continues the remaining two automatically, preserving their per-row actions.
                await Assertions.Expect(page.Locator(".media-import__activity--done")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator(".media-import__activity progress")).ToHaveJSPropertyAsync("value", 42);
                await Assertions.Expect(page.Locator(".media-import__activity-list li")).ToHaveCountAsync(42);
                await Assertions.Expect(page.Locator(".media-import__activity form")).ToHaveCountAsync(0);
                await Evidence.SaveAsync(page, "media-import-auto-batches");
                await Assertions.Expect(page.Locator(".media-import__activity-list li", new() { HasText = "Bulk Movie 30040" })).ToContainTextAsync("Added");
                await Assertions.Expect(page.Locator(".media-import__activity-list li", new() { HasText = "Bulk Movie 30041" })).ToContainTextAsync("Added, monitored");
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-media-import-continue");
                throw;
            }
        }
    }
}
