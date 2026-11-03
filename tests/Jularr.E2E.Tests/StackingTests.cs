using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // One stacking order for the whole app (the z tokens of Theme.scss): an open menu lies over what follows it, the phone menu over the floating pill.
    [TestClass]
    public sealed class StackingTests
    {
        // What the visitor sees at the middle of each element: the element on top there must be inside the expected layer.
        private const string TopmostInside = """
            ([layer, targets]) => {
                const over = document.querySelector(layer);
                return [...document.querySelectorAll(targets)].map(target => {
                    const box = target.getBoundingClientRect(), frame = over.getBoundingClientRect();
                    const x = box.x + box.width / 2, y = box.y + box.height / 2;
                    if (x < frame.left || x > frame.right || y < frame.top || y > frame.bottom) return null;
                    return over.contains(document.elementFromPoint(x, y));
                }).filter(result => result !== null);
            }
            """;

        [TestMethod]
        [Timeout(300_000)]
        public async Task Phone_OpenHeroMenu_LiesOverTheTabs_AndTheMenu_OverTheFloatingPill()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (9801, 1, 'Layered Film', 2020);""");
            await jularr.SeedLibraryFilesAsync();
            await page.SetViewportSizeAsync(390, 844);

            try
            {
                // The hero's menu opens over the page's tabs, which come after the hero.
                await page.GotoAsync("/Details/9801");
                await page.Locator(".media-hero details.action-menu > summary").First.ClickAsync();
                var tabs = await page.EvaluateAsync<bool[]>(TopmostInside, new[] { ".media-hero details[open] .action-menu__panel", ".navigation-tabs__link" });
                Assert.IsTrue(tabs.Length > 0, "the open menu covers some tab");
                CollectionAssert.DoesNotContain(tabs, false, "a tab lies over the open menu");

                // The phone menu covers the page's floating pill.
                await page.GotoAsync("/Admin/Works");
                await Assertions.Expect(page.Locator(".floating-pill:not([hidden])").First).ToBeVisibleAsync();
                await page.Locator(".app-header .app-menu-toggle").ClickAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('.app-sidebar').getBoundingClientRect().left >= 0");
                var pill = await page.EvaluateAsync<bool[]>(TopmostInside, new[] { ".app-sidebar", ".floating-pill:not([hidden])" });
                Assert.IsTrue(pill.Length > 0, "the phone menu covers the floating pill");
                CollectionAssert.DoesNotContain(pill, false, "the floating pill lies over the phone menu");
                await Evidence.SaveAsync(page, "stacking-phone-menu");
            }
            catch
            {
                await Evidence.SaveAsync(page, "stacking-failed");
                throw;
            }
        }
    }
}
