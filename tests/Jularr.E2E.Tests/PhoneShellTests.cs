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
