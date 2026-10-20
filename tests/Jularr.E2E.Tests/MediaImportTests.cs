using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // A small mixed list on the Import page: previewed, the open book chosen, imported, and repeated without a second copy of anything.
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

        [TestMethod]
        [Timeout(300_000)]
        public async Task MixedList_IsPreviewedImportedAndRepeatedWithoutDuplicates()
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

                // The page sits in the Media area of the administration.
                await page.GotoAsync("/Admin/Import");
                await Assertions.Expect(page.Locator(".app-sidebar a[href='/Admin/Import']")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator("[name=Json]")).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "media-import-upload");

                // The list is previewed: three new, one open with a proposed book, a duplicate and an invalid entry.
                await page.Locator("[name=Json]").FillAsync(List);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Preview"));
                var summary = page.Locator(".media-import__summary");
                foreach (var (count, status) in new[] { ("3", "New"), ("1", "Open"), ("1", "Duplicate"), ("1", "Invalid") })
                {
                    await Assertions.Expect(summary.Locator(".tag").Filter(new() { HasText = status })).ToContainTextAsync(count);
                }

                var rows = page.Locator("main table tbody tr");
                await Assertions.Expect(rows).ToHaveCountAsync(6);
                await Assertions.Expect(rows.Nth(5)).ToContainTextAsync("Unknown key: bogus");
                await Assertions.Expect(rows.Nth(4)).ToContainTextAsync("Same as row 1");
                var book = rows.Nth(2);
                await Assertions.Expect(book.Locator("select[name='Choices[2].Choice']")).ToHaveValueAsync("openlibrary:OL66554W");
                await Assertions.Expect(book.Locator("input[type=checkbox][name='Choices[2].Include']")).Not.ToBeCheckedAsync();
                await Assertions.Expect(rows.Nth(0).Locator("input[type=checkbox][name='Choices[0].Include']")).ToBeCheckedAsync();
                await Evidence.SaveAsync(page, "media-import-preview");
                Assert.AreEqual(0, await page.Locator("main table tbody tr a[href^='/Admin/Works/']").CountAsync(), "The preview must not add anything.");

                // A narrow screen keeps the page in its width, the table scrolls on its own.
                await page.SetViewportSizeAsync(390, 844);
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"));
                await Evidence.SaveAsync(page, "media-import-preview-mobile");
                await page.SetViewportSizeAsync(1280, 720);

                // The person takes the proposed book too and imports; every row reports its own outcome.
                await book.Locator("input[type=checkbox][name='Choices[2].Include']").CheckAsync();
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Import selected"));
                await Assertions.Expect(page.Locator(".media-import__summary .tag").Filter(new() { HasText = "Added, monitored" })).ToContainTextAsync("4");
                await Assertions.Expect(page.Locator("main table tbody tr")).ToHaveCountAsync(4);
                await Assertions.Expect(page.Locator("main table tbody tr .tag", new() { HasText = "Searching" })).ToHaveCountAsync(4);
                await Evidence.SaveAsync(page, "media-import-result");
                await page.SetViewportSizeAsync(390, 844);
                await Evidence.SaveAsync(page, "media-import-result-mobile");
                await page.SetViewportSizeAsync(1280, 720);

                // The titles are in the library once, each with a target.
                await page.GotoAsync("/Admin/Works");
                foreach (var title in new[] { FakeServices.MovieTitle, FakeServices.SeriesTitle, FakeServices.BookTitle, FakeServices.AlbumTitle })
                {
                    await Assertions.Expect(page.Locator("main table tbody tr", new() { HasText = title })).ToHaveCountAsync(1);
                }

                await page.GotoAsync("/Admin/Wanted");
                foreach (var title in new[] { FakeServices.MovieTitle, FakeServices.SeriesTitle, FakeServices.BookTitle, FakeServices.AlbumTitle })
                {
                    await Assertions.Expect(page.Locator("main table tbody tr", new() { HasText = title })).ToHaveCountAsync(1);
                }

                // The same list again finds everything in the library; importing it changes nothing.
                await page.GotoAsync("/Admin/Import");
                await page.Locator("[name=Json]").FillAsync(List.Replace("{\"type\":\"book\",\"title\":\"Pride and Prejudice\",\"author\":\"Jane Austen\"}",
                    "{\"type\":\"book\",\"ids\":{\"openlibrary\":\"OL66554W\"}}", StringComparison.Ordinal));
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Preview"));
                await Assertions.Expect(page.Locator(".media-import__summary .tag").Filter(new() { HasText = "In library" })).ToContainTextAsync("4");
                await Assertions.Expect(page.Locator("main table tbody tr input[type=checkbox]")).ToHaveCountAsync(0);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Import selected"));
                await Assertions.Expect(page.Locator(".media-import__summary .tag").Filter(new() { HasText = "Skipped" })).ToContainTextAsync("6");
                await page.GotoAsync("/Admin/Works");
                await Assertions.Expect(page.Locator("main table tbody tr a[href^='/Admin/Works/']")).ToHaveCountAsync(4);
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-media-import");
                throw;
            }
        }
    }
}
