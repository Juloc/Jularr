using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The administration on a phone: the queue keeps room for its titles and the header trail stays reachable.
    [TestClass]
    public sealed class PhoneShellTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Phone_QueueRowKeepsItsTitle_AndTheHeaderTrailShowsItsFirstPartAndScrollsItsMenu()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);

                // A failed scan: its status cell carries a long reason that must not push the title out of the row.
                await jularr.RunSqlAsync("""
                    DO $$
                    DECLARE mount bigint; root bigint;
                    BEGIN
                        INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") VALUES ('Phone queue', 3, '/phone-queue', FALSE) RETURNING "Id" INTO mount;
                        INSERT INTO "LibraryRoots" ("Name", "MediaTypeId", "StorageMountId", "RelativePath") VALUES ('Phone queue library', 1, mount, 'phone') RETURNING "Id" INTO root;
                        INSERT INTO "Operations" ("OperationTypeId", "OperationStatusTypeId", "LibraryRootId", "ErrorKey", "FinishedAt")
                        VALUES (1, 4, root, 'storage_unavailable', now());
                    END $$;
                    """);

                foreach (var width in new[] { 375, 480, 600 })
                {
                    await page.SetViewportSizeAsync(width, 667);
                    await page.GotoAsync("/Admin/Activity");
                    var row = Steps.ListRow(page, "Phone queue library");
                    await Assertions.Expect(row).ToBeVisibleAsync();
                    await Assertions.Expect(row).ToContainTextAsync("not reachable");
                    var main = (await row.Locator(".list-cell--main").BoundingBoxAsync())!;
                    Assert.IsTrue(main.Width > 120, "The title column is " + main.Width + "px wide at " + width + "px.");
                    var title = (await row.Locator(".list-identity").BoundingBoxAsync())!;
                    Assert.IsTrue(title.Width > 60, "The title is " + title.Width + "px wide at " + width + "px.");
                }

                // The trail opens with its first part whole, and the open menu of the area fits the short screen and scrolls.
                await page.SetViewportSizeAsync(375, 667);
                await page.GotoAsync("/Admin/Downloads");
                var trail = page.Locator(".breadcrumb");
                var cut = await trail.EvaluateAsync<double>("""
                    trail => {
                        const first = [...trail.children].find(item => item.getBoundingClientRect().width > 0);
                        return trail.getBoundingClientRect().left - first.getBoundingClientRect().left;
                    }
                    """);
                Assert.IsTrue(cut <= 0.5, "The first part of the trail is cut by " + cut + "px.");

                await trail.Locator("details.link-menu summary").First.ClickAsync();
                var list = trail.Locator("details.link-menu[open] .link-menu__list");
                await Assertions.Expect(list).ToBeVisibleAsync();
                var bottom = (await list.BoundingBoxAsync())!;
                Assert.IsTrue(bottom.Y + bottom.Height <= 667 + 0.5, "The menu ends at " + (bottom.Y + bottom.Height) + "px of 667.");
                Assert.IsTrue(await list.EvaluateAsync<bool>("list => list.scrollHeight > list.clientHeight && getComputedStyle(list).overflowY === 'auto'"), "The menu does not scroll.");

                // A detail page with a very long title: the first part (the parent) stays whole and the title, the last part, ends in an ellipsis.
                await jularr.RunSqlAsync("""
                    INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear")
                    VALUES (8101, 1, 'An extraordinarily long film title that cannot possibly fit into the header of a phone next to its parent part of the trail', 2001);
                    """);
                await page.GotoAsync("/Admin/Works/8101");
                await Assertions.Expect(trail.Locator(".breadcrumb__item:last-child")).ToContainTextAsync("An extraordinarily long film title");
                var trailShape = await trail.EvaluateAsync<double[]>("""
                    trail => {
                        const shown = [...trail.children].filter(item => item.getBoundingClientRect().width > 0);
                        const first = shown[0].getBoundingClientRect();
                        const last = shown[shown.length - 1];
                        const text = last.querySelector(".truncated__text") ?? last;
                        const box = trail.getBoundingClientRect();
                        return [shown.length, first.left - box.left, box.right - last.getBoundingClientRect().right, text.scrollWidth - text.clientWidth,
                            getComputedStyle(text).textOverflow === 'ellipsis' ? 1 : 0, trail.scrollWidth - trail.clientWidth, trail.scrollLeft, shown[0].scrollWidth - shown[0].clientWidth];
                    }
                    """);
                Assert.AreEqual(2.0, trailShape[0], "The phone trail shows the parent and the title.");
                Assert.IsTrue(trailShape[1] >= -0.5, "The first part of the trail is cut by " + -trailShape[1] + "px.");
                Assert.IsTrue(trailShape[2] >= -0.5, "The title leaves the trail by " + -trailShape[2] + "px.");
                Assert.IsTrue(trailShape[3] > 0 && trailShape[4] == 1, "The title is not cut with an ellipsis.");
                Assert.IsTrue(trailShape[5] <= 1 && trailShape[6] == 0 && trailShape[7] <= 1.5, "The trail scrolls or its first part is clipped: " + string.Join(", ", trailShape));

                // The whole title is in the tip while it is cut.
                await trail.Locator(".breadcrumb__item:last-child .truncated").HoverAsync();
                var tip = trail.Locator(".breadcrumb__item:last-child .truncated__tip");
                await Assertions.Expect(tip).ToBeVisibleAsync();
                var tipBox = (await tip.BoundingBoxAsync())!;
                Assert.IsTrue(tipBox.X >= 0 && tipBox.X + tipBox.Width <= 375 && tipBox.Y > 30, $"The tip is at ({tipBox.X},{tipBox.Y},{tipBox.Width}).");
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "phone-shell");
                throw;
            }
        }
    }
}
