using System.Globalization;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The page of a title on a phone: the cover beside the title and one big action with the more menu, the first episodes in the first screen, the facts of an episode as tags
    // that wrap (nothing slides or is cut off), and a tap of at least 44x44px on every control. Wide screens keep the row of actions.
    [TestClass]
    public sealed class WorkPagePhoneTests
    {
        private const string Seed = """
            UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" IN ('Acquisition', 'Requests');
            INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") VALUES ('Phone', 3, '/tmp/phone', FALSE);
            INSERT INTO "LibraryRoots" ("Name", "MediaTypeId", "StorageMountId", "RelativePath") SELECT 'Series', 2, "Id", 'series' FROM "StorageMounts" WHERE "Name" = 'Phone';
            INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (8201, 2, 'Phone Series', 2019);
            INSERT INTO "WorkEditions" ("WorkId") VALUES (8201);
            INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId", "Name") SELECT 8201, "Id", 'v' FROM "WorkEditions" WHERE "WorkId" = 8201;
            INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (8201, 8201);
            INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number") SELECT 8201, 1, season FROM generate_series(1, 2) AS season;
            INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "Title", "ReleasedOn")
            SELECT 8201, season."Id", 2, episode, 'Episode title number ' || episode, current_date - (episode * 7 + season."Number"::integer * 100)
            FROM "WorkUnits" AS season, generate_series(1, 6) AS episode WHERE season."WorkId" = 8201 AND season."WorkUnitTypeId" = 1;
            INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "MediaAssetTypeId", "WorkUnitId")
            SELECT 8201, version."Id", 1, unit."Id" FROM "WorkUnits" AS unit, "WorkVersions" AS version
            WHERE unit."WorkId" = 8201 AND unit."WorkUnitTypeId" = 2 AND version."WorkId" = 8201 AND unit."Number" <= 4;
            INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "LastSeenAt", "MediaAssetId", "QualityKey", "ReleaseGroup", "DurationMs")
            SELECT root."Id", 'Phone Series/S' || parent."Number" || 'E' || unit."Number" || '.mkv', 1500000000, now(), now(), asset."Id", 'WEBDL-1080p', 'GRP', 1500000
            FROM "MediaAssets" AS asset JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId" JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId", "LibraryRoots" AS root
            WHERE asset."WorkId" = 8201 AND root."Name" = 'Series';
            """;

        [TestMethod]
        [Timeout(300_000)]
        public async Task WorkPage_OnAPhone_ShowsOneActionAndTheFirstEpisodes_AndKeepsTheRowOnWideScreens()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await jularr.RunSqlAsync(Seed);

                // Phone: the cover is small and beside the title; the one big action and the more menu are 44px high, the others are in the menu.
                await page.SetViewportSizeAsync(375, 812);
                await page.GotoAsync("/Admin/Works/8201");
                await Assertions.Expect(page.Locator(".work-hero h1")).ToHaveTextAsync("Phone Series");
                var main = page.Locator(".work-action--main button");
                var more = page.Locator(".work-action--more summary");
                await Assertions.Expect(main).ToBeVisibleAsync();
                await Assertions.Expect(more).ToBeVisibleAsync();
                Assert.IsTrue((await main.BoundingBoxAsync())!.Height >= 43.5, "The main action is lower than 44px.");
                var moreBox = (await more.BoundingBoxAsync())!;
                Assert.IsTrue(moreBox.Width >= 43.5 && moreBox.Height >= 43.5, $"The more button is {moreBox.Width}x{moreBox.Height}.");
                await Assertions.Expect(page.Locator(".work-action--aside").First).ToBeHiddenAsync();
                var cover = (await page.Locator(".work-cover").BoundingBoxAsync())!;
                var title = (await page.Locator(".work-title h1").BoundingBoxAsync())!;
                Assert.IsTrue(cover.Width <= 80 && title.X >= cover.X + cover.Width - 1, "The cover is not small and beside the title.");

                // The more menu: it opens over the page with every other action, each 44px high, and Escape closes it.
                await more.ClickAsync();
                var panel = page.Locator(".work-hero .action-menu__panel");
                await Assertions.Expect(panel).ToBeVisibleAsync();
                var panelBox = (await panel.BoundingBoxAsync())!;
                Assert.IsTrue(panelBox.X >= 0 && panelBox.X + panelBox.Width <= 375.5, "The menu leaves the screen.");
                foreach (var item in await panel.Locator(".action-menu__item").AllAsync())
                {
                    Assert.IsTrue((await item.BoundingBoxAsync())!.Height >= 43.5, "A menu item is lower than 44px.");
                }

                await Assertions.Expect(panel.Locator(".action-menu__item")).ToHaveCountAsync(4);
                await Evidence.SaveAsync(page, "work-phone-menu-open");
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(panel).ToBeHiddenAsync();

                // The first episode is in the first screen, with its facts as tags that stay inside the width.
                var firstEpisode = (await page.Locator(".work-strip__main").First.BoundingBoxAsync())!;
                Assert.IsTrue(firstEpisode.Y + firstEpisode.Height <= 812, "The first episode starts below the first screen: " + (firstEpisode.Y + firstEpisode.Height));
                Assert.IsFalse(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1"), "The page scrolls sideways.");
                Assert.IsFalse(await page.Locator(".work-strips").First.EvaluateAsync<bool>("element => element.scrollWidth > element.clientWidth + 1"), "The facts slide sideways.");
                Assert.IsTrue(await page.Locator(".work-strips").First.EvaluateAsync<bool>("""
                    element => [...element.querySelectorAll('.work-strip__facts .work-fact, .work-strip__main > *')].every(item => {
                        const box = item.getBoundingClientRect(), bounds = element.getBoundingClientRect();
                        return box.width === 0 || (box.left >= bounds.left - 0.5 && box.right <= bounds.right + 0.5);
                    })
                    """), "A fact or a control leaves the width.");

                // The tabs show every label whole (they may slide, then the page says so by showing the next one cut at the edge, never a label that is cut by its own clip).
                Assert.IsTrue(await page.Locator(".navigation-tabs").First.EvaluateAsync<bool>("nav => nav.scrollWidth <= nav.clientWidth + 1"), "The tabs run wider than the screen.");

                // A finger reaches 44x44px on each control of the page.
                var problems = new List<string>();
                foreach (var selector in new[] { ".work-action--main button", ".work-action--more summary", ".work-strip .work-switch", ".work-season__toggle", ".navigation-tabs__link", ".work-strip .work-icon" })
                {
                    var controls = page.Locator(selector);
                    for (var position = 0; position < Math.Min(await controls.CountAsync(), 3); position++)
                    {
                        if (!await controls.Nth(position).IsVisibleAsync())
                        {
                            continue;
                        }

                        var extent = await controls.Nth(position).EvaluateAsync<string[]>(PhoneTapTargetsTests.Extent);
                        var width = double.Parse(extent[0], CultureInfo.InvariantCulture);
                        var height = double.Parse(extent[1], CultureInfo.InvariantCulture);
                        if (width < 43.5 || height < 43.5)
                        {
                            problems.Add($"{selector} #{position} reaches {width}x{height}: " + await controls.Nth(position).EvaluateAsync<string>("""
                                control => { const after = getComputedStyle(control, '::after'), box = control.getBoundingClientRect(); const above = document.elementFromPoint(box.left + box.width / 2, box.top - 2); return after.height + ' / ' + after.position + ' / ' + after.content + ' / box ' + box.height + ' / above ' + (above?.className ?? 'nothing') + ' ' + (above?.tagName ?? ''); }
                                """));
                        }
                    }
                }

                Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
                await page.EvaluateAsync("() => window.scrollTo(0, 0)");
                await Evidence.SaveAsync(page, "work-phone-375");

                // Tablet and desktop: the row of actions stays, the more menu is not shown, and nothing runs wider than the screen.
                foreach (var (width, height) in new[] { (768, 1024), (1440, 900) })
                {
                    await page.SetViewportSizeAsync(width, height);
                    await page.GotoAsync("/Admin/Works/8201");
                    await Assertions.Expect(page.Locator(".work-hero h1")).ToHaveTextAsync("Phone Series");
                    await Assertions.Expect(page.Locator(".work-action--more summary")).ToBeHiddenAsync();
                    await Assertions.Expect(page.Locator(".work-action--aside").First).ToBeVisibleAsync();
                    Assert.IsFalse(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1"), $"{width}: the page scrolls sideways.");
                    await Evidence.SaveAsync(page, $"work-phone-{width}");
                }

                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-work-phone");
                throw;
            }
        }
    }
}
