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
                await Assertions.Expect(dialog.Locator("tr.search-row[data-index]:visible")).ToHaveCountAsync(1);
                await Assertions.Expect(dialog.GetByRole(AriaRole.Status).First).ToContainTextAsync("1 of 3 releases");
                await Evidence.SaveAsync(page, "wanted-search-filtered-desktop");
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Slow catalog" }).ClickAsync();
                await Assertions.Expect(dialog.Locator("tr.search-row[data-index]:visible")).ToHaveCountAsync(3);
                Assert.AreEqual(calls, sources.Calls, "Filtering asked the sources again.");

                // The filters only show or hide what is loaded: a language, a size nothing has, and the order across the sources; the bar itself never moves.
                async Task ChooseAsync(string current, string option)
                {
                    await dialog.GetByRole(AriaRole.Button, new() { Name = current, Exact = true }).ClickAsync();
                    await page.GetByRole(AriaRole.Option, new() { Name = option, Exact = true }).ClickAsync();
                }

                var rows = dialog.Locator("tr.search-row[data-index]:visible");
                var bar = await dialog.Locator(".search-filters").BoundingBoxAsync();
                await ChooseAsync("All languages", "German");
                await Assertions.Expect(rows).ToHaveCountAsync(1);
                await Assertions.Expect(rows.First).ToContainTextAsync("Moby Dick");
                await Assertions.Expect(dialog.GetByRole(AriaRole.Status).First).ToContainTextAsync("1 of 3 releases");
                await Evidence.SaveAsync(page, "wanted-search-language-filter-desktop");
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Clear filters" }).ClickAsync();
                await Assertions.Expect(rows).ToHaveCountAsync(3);
                await ChooseAsync("Any size", "Over 20 GB");
                await Assertions.Expect(rows).ToHaveCountAsync(0);
                await Assertions.Expect(dialog.Locator("[data-search-nomatch]")).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "wanted-search-no-match-desktop");
                var after = await dialog.Locator(".search-filters").BoundingBoxAsync();
                Assert.AreEqual((bar!.X, bar.Y, bar.Width, bar.Height), (after!.X, after.Y, after.Width, after.Height), "The filter bar moved while filtering.");
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Clear filters" }).ClickAsync();
                await ChooseAsync("Sort", "Best score");
                var order = await dialog.Locator("tbody[data-merged] tr.search-row").EvaluateAllAsync<string[]>("rows => rows.map(row => row.dataset.score)");
                CollectionAssert.AreEqual(new[] { "60", "60", "0" }, order);
                await dialog.Locator("tr.search-row[data-index]").Nth(2).EvaluateAsync("row => row.dataset.size = '9000000000'");
                await ChooseAsync("Sort", "Largest");
                Assert.AreEqual("9000000000", await dialog.Locator("tbody[data-merged] tr.search-row").First.GetAttributeAsync("data-size"));
                await Evidence.SaveAsync(page, "wanted-search-sorted-desktop");
                await ChooseAsync("Sort", "Sort by source");
                Assert.AreEqual(0, await dialog.Locator("tbody[data-merged]").CountAsync());

                // The title field takes words in any order (accents do not matter), the decision shows one kind, the qualities one or several; the bar does not move.
                var title = dialog.GetByRole(AriaRole.Searchbox, new() { Name = "Search in title" });
                await title.FillAsync("melville DICK");
                await Assertions.Expect(rows).ToHaveCountAsync(1);
                await Assertions.Expect(rows.First).ToContainTextAsync("Moby Dick");
                await Assertions.Expect(dialog.GetByRole(AriaRole.Status).First).ToContainTextAsync("1 of 3 releases");
                await title.FillAsync("prejudice pride");
                await Assertions.Expect(rows).ToHaveCountAsync(2);
                await title.FillAsync("zzz");
                await Assertions.Expect(dialog.Locator("[data-search-nomatch]")).ToBeVisibleAsync();
                var typed = await dialog.Locator(".search-filters").BoundingBoxAsync();
                Assert.AreEqual((bar!.X, bar.Y, bar.Width, bar.Height), (typed!.X, typed.Y, typed.Width, typed.Height), "The filter bar moved while typing.");
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Clear filters" }).ClickAsync();
                await Assertions.Expect(title).ToHaveValueAsync("");
                await Assertions.Expect(rows).ToHaveCountAsync(3);
                await ChooseAsync("All decisions", "Rejected");
                await Assertions.Expect(rows).ToHaveCountAsync(1);
                await Assertions.Expect(rows.First).ToContainTextAsync("Moby Dick");
                await ChooseAsync("All decisions", "Review");
                await Assertions.Expect(rows).ToHaveCountAsync(2);
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Clear filters" }).ClickAsync();

                // The releases here all say EPUB, so two of them get another quality, and one none.
                await dialog.Locator("tr.search-row[data-index]").Nth(0).EvaluateAsync("row => row.dataset.quality = 'PDF'");
                await dialog.Locator("tr.search-row[data-index]").Nth(1).EvaluateAsync("row => row.dataset.quality = ''");
                await title.FillAsync("e");
                await title.FillAsync("");
                var qualities = dialog.GetByRole(AriaRole.Button, new() { Name = "All qualities", Exact = true });
                await qualities.ClickAsync();
                await page.GetByRole(AriaRole.Checkbox, new() { Name = "PDF", Exact = true }).CheckAsync();
                await Assertions.Expect(rows).ToHaveCountAsync(1);
                await Assertions.Expect(rows.First).ToContainTextAsync("Pride and Prejudice");
                await page.GetByRole(AriaRole.Checkbox, new() { Name = "Unknown", Exact = true }).CheckAsync();
                await Assertions.Expect(rows).ToHaveCountAsync(2);
                await Assertions.Expect(qualities).ToContainTextAsync("2 qualities");
                await Evidence.SaveAsync(page, "wanted-search-quality-filter-desktop");
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(page.Locator("dialog.search-dialog")).ToHaveCountAsync(1);
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Clear filters" }).ClickAsync();
                await Assertions.Expect(rows).ToHaveCountAsync(3);
                await Assertions.Expect(qualities).ToContainTextAsync("All qualities");
                Assert.AreEqual(0, await page.GetByRole(AriaRole.Checkbox, new() { Checked = true }).CountAsync());
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

        [TestMethod]
        [Timeout(300_000)]
        public async Task SeasonDialog_SearchesTheWholeSeason_CountsInTheSingular_AndKeepsItsHeadClear()
        {
            await using var sources = await Catalogs.StartAsync();
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);

            // Season 6 of a series, all three episodes aired and missing; one indexer answers with the season pack, two of its episodes and one of season 5.
            await jularr.RunSqlAsync($$"""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (601, 2, 'Jularr Season Show', 2020);
                INSERT INTO "WorkUnits" ("Id", "WorkId", "WorkUnitTypeId", "Number") VALUES (6010, 601, 1, 6);
                INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "ReleasedOn")
                SELECT 601, 6010, 2, episode."Number", date '2020-01-01' + episode."Number" FROM generate_series(1, 3) AS episode ("Number");
                INSERT INTO "WantedItems" ("Id", "WorkId", "NextSearchAt") VALUES (601, 601, now() + interval '3 days');
                INSERT INTO "AcquisitionConnections" ("Name", "AcquisitionConnectionTypeId", "BaseUrl") VALUES ('Season indexer', 1, '{{sources.Address}}/newznab/');
                """);

            try
            {
                await page.SetViewportSizeAsync(1280, 900);
                await page.GotoAsync("/Admin/Works/601");
                await page.GetByRole(AriaRole.Link, new() { Name = "Interactive search for this season" }).ClickAsync();
                var dialog = page.Locator("dialog.search-dialog[open]");
                await Assertions.Expect(dialog.GetByRole(AriaRole.Heading, new() { Name = "Jularr Season Show" })).ToBeVisibleAsync();

                // The season is asked, no episode: the empty field reads "All", not an example number.
                await Assertions.Expect(dialog.Locator("[name=ParentUnitNumber]")).ToHaveValueAsync("6");
                var episode = dialog.Locator("[name=UnitNumber]");
                await Assertions.Expect(episode).ToHaveValueAsync("");
                await Assertions.Expect(episode).ToHaveAttributeAsync("placeholder", "All");

                // The X has the first focus, so no field's tip opens by itself; an opened one does not cover the title, and nothing covers it.
                await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Close" })).ToBeFocusedAsync();
                await Assertions.Expect(dialog.Locator(".ui-field__tip:visible")).ToHaveCountAsync(0);
                await dialog.Locator(".search-scope .ui-field__info").First.HoverAsync();
                var tip = dialog.Locator(".ui-field__tip:visible");
                await Assertions.Expect(tip).ToHaveCountAsync(1);
                var title = await dialog.GetByRole(AriaRole.Heading, new() { Name = "Jularr Season Show" }).BoundingBoxAsync();
                var box = await tip.BoundingBoxAsync();
                Assert.IsFalse(box!.X < title!.X + title.Width && title.X < box.X + box.Width && box.Y < title.Y + title.Height && title.Y < box.Y + box.Height, "The tip covers the dialog's title.");
                Assert.IsTrue(await tip.EvaluateAsync<bool>("tip => { const box = tip.getBoundingClientRect(); return document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2)?.closest('.ui-field__tip') === tip; }"),
                    "Something covers the tip.");
                await Evidence.SaveAsync(page, "wanted-search-season-tip-desktop");
                await page.Mouse.MoveAsync(5, 5);

                // Every episode of the season is a release for it, the season pack too; only the other season is wrong. One source is counted in the singular.
                await Assertions.Expect(dialog.Locator("tbody[data-state=done]")).ToHaveCountAsync(1);
                await Assertions.Expect(dialog.GetByRole(AriaRole.Status).First).ToHaveTextAsync("4 releases from 1 source");
                var rows = dialog.Locator("tr.search-row[data-index]");
                await Assertions.Expect(rows).ToHaveCountAsync(4);
                await Assertions.Expect(rows.Filter(new() { HasText = "Wrong episode" })).ToHaveCountAsync(0);
                await Assertions.Expect(rows.Filter(new() { HasText = "S05E01" })).ToContainTextAsync("Wrong season");
                await Evidence.SaveAsync(page, "wanted-search-season-desktop");

                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());

                // On a touch phone the dialog fits, no tip opens by itself, and a tap on the (i) opens it.
                await using var phone = await session.Context.Browser!.NewContextAsync(new()
                {
                    BaseURL = jularr.Address.ToString(), ViewportSize = new() { Width = 390, Height = 844 }, HasTouch = true, IsMobile = true, StorageState = await session.Context.StorageStateAsync()
                });
                var touch = await phone.NewPageAsync();
                await touch.GotoAsync("/Admin/Works/601");
                await touch.GetByRole(AriaRole.Link, new() { Name = "Interactive search for this season" }).TapAsync();
                var small = touch.Locator("dialog.search-dialog[open]");
                await Assertions.Expect(small.Locator("tbody[data-state=done]")).ToHaveCountAsync(1);
                await Assertions.Expect(small.Locator(".ui-field__tip:visible")).ToHaveCountAsync(0);
                Assert.IsFalse(await small.EvaluateAsync<bool>("element => element.scrollWidth > element.clientWidth + 1 || document.body.scrollWidth > innerWidth + 1"));
                await Evidence.SaveAsync(touch, "wanted-search-season-phone");
                await small.Locator(".search-scope .ui-field__info").First.TapAsync();
                await Assertions.Expect(small.Locator(".ui-field__tip:visible")).ToHaveCountAsync(1);
                await Evidence.SaveAsync(touch, "wanted-search-season-tip-phone");
            }
            catch
            {
                await Evidence.SaveAsync(page, nameof(SeasonDialog_SearchesTheWholeSeason_CountsInTheSingular_AndKeepsItsHeadClear));
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
                        {"id":2701,"title":"Moby Dick","authors":[{"name":"Melville, Herman"}],"languages":["de"],"formats":{"application/epub+zip":"https://www.gutenberg.org/ebooks/2701.epub3.images"}}]}
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
                // A Newznab indexer of TV releases: its capabilities, and the same releases for every search.
                app.MapGet("/newznab/api", (HttpContext http) =>
                {
                    if (http.Request.Query["t"] == "caps")
                    {
                        return Results.Text("""
                            <caps><server title="E2E" /><limits max="100" default="100" />
                              <searching><search available="yes" supportedParams="q" /><tv-search available="yes" supportedParams="q,season,ep" /></searching>
                              <categories><category id="5000" name="TV"><subcat id="5040" name="HD" /></category></categories></caps>
                            """, "application/xml");
                    }

                    Interlocked.Increment(ref sources._calls);
                    var items = string.Concat(new[] { "S06", "S06E01", "S06E02", "S05E01" }.Select((name, index) =>
                        $"""<item><title>Jularr.Season.Show.{name}.1080p.WEB-DL-GRP</title><guid>season-{index}</guid><pubDate>Mon, 05 Oct 2026 10:00:00 +0000</pubDate><category>5040</category><enclosure url="{sources.Address}/newznab/get/{index}" length="2000000000" /></item>"""));
                    return Results.Text("<rss><channel>" + items + "</channel></rss>", "application/xml");
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
