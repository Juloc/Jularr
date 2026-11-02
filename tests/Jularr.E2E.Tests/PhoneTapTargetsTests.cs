using System.Globalization;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Owner's decision 2 Nov: on a phone every tap target takes at least 44x44px (its visual size may stay smaller; HitArea.scss). The test measures what a finger reaches:
    // from the centre of the control it walks along both axes as long as the element under the point is the control (or its label), so an overlay or padding counts and a neighbour's overlay does not.
    // Where 44px would make two targets overlap, the largest size that does not is the documented limit of that row.
    [TestClass]
    public sealed class PhoneTapTargetsTests
    {
        private const string Extent = """
            control => {
                control.scrollIntoView({ block: 'center', inline: 'center' });
                const box = control.getBoundingClientRect();
                const cx = box.left + box.width / 2, cy = box.top + box.height / 2;
                const owner = control.closest('label') ?? control;
                const hit = (x, y) => { const found = document.elementFromPoint(x, y); return found !== null && (control.contains(found) || owner.contains(found)); };
                if (!hit(cx, cy)) return [0, 0, document.elementFromPoint(cx, cy)?.className ?? 'nothing'];
                let left = 0, right = 0, up = 0, down = 0;
                while (left < 60 && hit(cx - left - 1, cy)) left++;
                while (right < 60 && hit(cx + right + 1, cy)) right++;
                while (up < 60 && hit(cx, cy - up - 1)) up++;
                while (down < 60 && hit(cx, cy + down + 1)) down++;
                return [left + right + 1, up + down + 1, ''].map(String);
            }
            """;

        // Path, control, the width and the height its tap must reach, and why a side stays below 44.
        private static readonly (string Path, string Selector, double Width, double Height, string Limit)[] Targets =
        [
            ("/", ".app-header .app-avatar", 40, 44, "header icons sit 8px apart"),
            ("/", ".app-header .app-notifications", 42, 44, "header icons sit 8px apart"),
            ("/", ".app-header .app-search__icon", 42, 44, "header icons sit 8px apart"),
            ("/", ".app-header .app-menu-toggle", 42, 44, "header icons sit 8px apart"),
            ("/", ".app-header .theme-toggle", 42, 44, "header icons sit 8px apart"),
            ("/Calendar", "a.calendar__date", 44, 44, ""),
            ("/Admin/Wanted", "input.list-select", 44, 44, ""),
            ("/Admin/Wanted", "a.list-identity__title", 44, 44, ""),
            ("/Admin/Works", "input.list-select", 44, 44, ""),
            ("/Admin/Works", "button.work-switch", 44, 44, ""),
            ("/Admin/Jobs", "button.list-identity__title", 44, 44, ""),
            ("/Admin/Jobs", "a.navigation-tabs__link", 44, 44, ""),
            ("/Admin/Localization", "a.locale-link", 44, 44, ""),
            ("/Admin/Localization", ".action-dock .button--icon", 38, 44, "the dock's steps sit 6px apart"),
            ("/Admin/Instance", "button.ui-field__info", 44, 38, "6px to its own field, 16px to the one above"),
            ("/Admin/Instance", "button.ui-field__step", 34, 19, "two steps stacked in one 28px pill"),
            ("/Admin/Instance/Modules", "label.toggle", 44, 44, ""),
            ("/Details/8001", "a.details-block__all", 44, 44, ""),
            ("/Details/8001?tab=related", "a.relation__title", 44, 44, ""),
            ("/Admin/Downloads", "a.downloads-icon", 38, 44, "icon buttons sit 6px apart"),
            ("/Admin/Downloads", ".downloads-tabs a.navigation-tabs__link", 44, 44, "")
        ];

        [TestMethod]
        [Timeout(900_000)]
        public async Task Phone_TapTargetsReachTheirHitArea()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await jularr.RunSqlAsync("""
                    INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (8001, 1, 'Source Film', 2001), (8002, 1, 'Related Film', 2002);
                    INSERT INTO "WorkRelations" ("WorkId", "RelatedWorkId", "WorkRelationTypeId", "SourceKey") VALUES (8001, 8002, 1, 'tmdb');
                    INSERT INTO "WantedItems" ("WorkId") VALUES (8001), (8002);
                    """);
                await page.SetViewportSizeAsync(375, 812);

                var problems = new List<string>();
                var measured = new List<string>();
                foreach (var group in Targets.GroupBy(target => target.Path))
                {
                    await page.GotoAsync(group.Key);
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    foreach (var (path, selector, width, height, limit) in group)
                    {
                        var controls = page.Locator(selector);
                        var count = await controls.CountAsync();
                        var measuredControls = 0;
                        for (var position = 0; position < count && measuredControls < 4; position++)
                        {
                            var control = controls.Nth(position);
                            if (!await control.IsVisibleAsync())
                            {
                                continue;
                            }

                            measuredControls++;
                            var extent = await control.EvaluateAsync<string[]>(Extent);
                            var reachedWidth = double.Parse(extent[0], CultureInfo.InvariantCulture);
                            var reachedHeight = double.Parse(extent[1], CultureInfo.InvariantCulture);
                            measured.Add($"{path} {selector} #{position}: {reachedWidth}x{reachedHeight}");
                            if (reachedWidth < width || reachedHeight < height)
                            {
                                problems.Add($"{path} {selector} #{position} reaches {reachedWidth}x{reachedHeight}, needs {width}x{height}" + (limit.Length > 0 ? " (" + limit + ")" : "") + (extent[2].Length > 0 ? "; the tap at its centre goes to " + extent[2] : ""));
                            }
                        }

                        if (measuredControls == 0)
                        {
                            problems.Add($"{path} {selector}: no visible control to measure.");
                        }
                    }
                }

                Evidence.Log(string.Join(Environment.NewLine, measured));
                Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
            }
            catch
            {
                await Evidence.SaveAsync(page, "phone-tap-targets");
                throw;
            }
        }
    }
}
