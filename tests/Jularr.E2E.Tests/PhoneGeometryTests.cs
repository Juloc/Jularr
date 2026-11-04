using System.Text.Json;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // What a phone shows fits: fields as high as a tap (44px), the buttons of a card apart, the end of a page clear of the floating pill, a bottom sheet with its
    // footer on the screen, calendar strips inside their day and the media-type and status tabs clear of the tools beside them. The desktop keeps its sizes.
    // The Reader's panels close on Escape wherever the click inside them left the focus.
    [TestClass]
    public sealed class PhoneGeometryTests
    {
        // The visible fields of the page: the frame a field draws (its .ui-field__control, else the control itself) and its height.
        private const string Fields = """
            () => [...document.querySelectorAll('.ui-field__control > :is(input, .ui-dropdown__button), .search-box__input')]
                .filter(control => control.getClientRects().length > 0 && !control.closest('.app-header, dialog:not([open])'))
                .map(control => { const frame = control.closest('.ui-field__control') ?? control; return { name: control.name || control.className, frame: frame.getBoundingClientRect().height, control: control.getBoundingClientRect().height }; })
            """;

        // The gaps between the buttons placed straight in a card: to the next in the same line, or to the line below.
        private const string CardButtonGaps = """
            () => [...document.querySelectorAll('.ui-section > .button')].filter(button => button.getClientRects().length > 0).flatMap((button, index, all) => {
                const next = all[index + 1];
                if (!next || next.parentElement !== button.parentElement) return [];
                const a = button.getBoundingClientRect(), b = next.getBoundingClientRect();
                return [Math.abs(a.top - b.top) < 1 ? b.left - a.right : b.top - a.bottom];
            })
            """;

        // The share of each tab's visible part (inside its scrolling row) that another control of the page covers.
        private const string CoveredTabs = """
            () => {
                const tabs = [...document.querySelectorAll('#app-content :is(.navigation-tabs__link, .capsule-bar__all, .capsule-group__master)')].filter(tab => tab.getClientRects().length > 0);
                const others = [...document.querySelectorAll('#app-content :is(a, button, select, summary, input)')].filter(other => other.getClientRects().length > 0 && !other.closest('.navigation-tabs, .capsule-bar__areas, details:not([open]) > :not(summary)'));
                const clip = element => {
                    let box = element.getBoundingClientRect();
                    for (let parent = element.parentElement; parent; parent = parent.parentElement) {
                        if (getComputedStyle(parent).overflowX === 'visible') continue;
                        const edge = parent.getBoundingClientRect();
                        box = { left: Math.max(box.left, edge.left), right: Math.min(box.right, edge.right), top: Math.max(box.top, edge.top), bottom: Math.min(box.bottom, edge.bottom) };
                    }
                    return box;
                };
                return tabs.flatMap(tab => {
                    const seen = clip(tab);
                    if (seen.right - seen.left < 1 || seen.bottom - seen.top < 1) return [];
                    return others.filter(other => !tab.contains(other) && !other.contains(tab)).flatMap(other => {
                        const box = other.getBoundingClientRect();
                        const width = Math.min(seen.right, box.right) - Math.max(seen.left, box.left), height = Math.min(seen.bottom, box.bottom) - Math.max(seen.top, box.top);
                        return width > 0.5 && height > 0.5 ? [`${tab.textContent.trim() || tab.getAttribute('aria-label')} under ${other.className || other.tagName} (${width.toFixed(1)}x${height.toFixed(1)})`] : [];
                    });
                });
            }
            """;

        [TestMethod]
        [Timeout(900_000)]
        public async Task Phone_FieldsButtonsPageEndSheetsCalendarAndTabsFit_DesktopKeepsItsSizes_ReaderPanelClosesOnEscape()
        {
            using var folders = new Folders("Books");
            ReaderV3Tests.WriteReaderEpub(Path.Combine(folders.Library, "Books", "phone-geometry.epub"), "The Midnight Archive", "Local fixture", 3, 20);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await ReadingPathTests.AddBookAsync(page, folders, "The Midnight Archive");
                await jularr.RunSqlAsync("""
                    INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear", "ReleasedOn") VALUES
                        (8101, 1, 'A Very Long Film Title For The Strip', 2026, current_date), (8102, 1, 'Second Release', 2026, current_date), (8103, 1, 'Third Release', 2026, current_date);
                    INSERT INTO "WatchlistEntries" ("ProfileId", "WorkId") SELECT profile."Id", work."Id" FROM "Profiles" AS profile CROSS JOIN "Works" AS work WHERE work."Id" BETWEEN 8101 AND 8103;
                    """);

                var problems = new List<string>();
                foreach (var (width, height) in new[] { (375, 812), (390, 844) })
                {
                    await page.SetViewportSizeAsync(width, height);

                    // Form fields and search fields are 44px high, the control filling its frame (the toolbar selects take a 44px tap: PhoneTapTargetsTests).
                    foreach (var path in new[] { "/Settings", "/Admin/Instance", "/Admin/Works", "/Calendar" })
                    {
                        await page.GotoAsync(path);
                        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                        var fields = await page.EvaluateAsync<JsonElement>(Fields);
                        Assert.IsTrue(fields.GetArrayLength() > 0, $"{width} {path}: no field to measure.");
                        foreach (var field in fields.EnumerateArray())
                        {
                            var frame = field.GetProperty("frame").GetDouble();
                            var control = field.GetProperty("control").GetDouble();
                            if (frame < 44 || control < 42)
                            {
                                problems.Add($"{width} {path} {field.GetProperty("name").GetString()}: frame {frame}, control {control}.");
                            }
                        }

                        if (width == 390) await Evidence.SaveAsync(page, "phone-geometry-390" + path.Replace('/', '-'));
                    }

                    // The buttons placed straight in a card (Settings: Account, Admin) stand 8px apart.
                    await page.GotoAsync("/Settings");
                    var gaps = await page.EvaluateAsync<double[]>(CardButtonGaps);
                    Assert.IsTrue(gaps.Length >= 2, $"{width}: the Settings cards have no buttons to measure.");
                    problems.AddRange(gaps.Where(gap => gap < 7.5).Select(gap => $"{width} /Settings: two card buttons are {gap}px apart."));

                    // At the end of a page the floating pill leaves the last line of the content free (the schedule: its history of tasks).
                    await page.GotoAsync("/Admin/Jobs");
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    await page.EvaluateAsync("() => window.scrollTo(0, document.documentElement.scrollHeight)");
                    await page.WaitForTimeoutAsync(300);
                    var allBox = (await page.Locator(".jobs-history-block").BoundingBoxAsync())!;
                    var dockBox = (await page.Locator(".floating-pill.action-dock").Filter(new() { Visible = true }).BoundingBoxAsync())!;
                    if (allBox.Y + allBox.Height > dockBox.Y)
                    {
                        problems.Add($"{width} /Admin/Jobs: the history ends at {allBox.Y + allBox.Height}, the floating pill starts at {dockBox.Y}.");
                    }

                    if (width == 390) await ScreenAsync(page, "phone-geometry-390-jobs-end");

                    // A bottom sheet stands with its footer on the screen once it has slid in: a job's details and a filter.
                    await page.Locator(".jobs-list .list-identity__title").First.ClickAsync();
                    await SheetFitsAsync(page, problems, $"{width} job details", height);
                    if (width == 390) await ScreenAsync(page, "phone-geometry-390-job-drawer");
                    await page.Keyboard.PressAsync("Escape");
                    await page.GotoAsync("/Admin/Works");
                    await page.Locator("[data-drawer-open=works-filter]").ClickAsync();
                    await SheetFitsAsync(page, problems, $"{width} works filter", height);
                    await page.Keyboard.PressAsync("Escape");

                    // A calendar strip stays inside its day.
                    await page.GotoAsync("/Calendar");
                    var strips = await page.EvaluateAsync<double[][]>("() => [...document.querySelectorAll('.calendar__strip')].map(strip => { const box = strip.getBoundingClientRect(), cell = strip.closest('.calendar__cell').getBoundingClientRect(); return [box.left - cell.left, cell.right - box.right, box.width]; })");
                    Assert.IsTrue(strips.Length >= 3, $"{width}: the calendar shows no strips.");
                    problems.AddRange(strips.Where(strip => strip[0] < 0 || strip[1] < 0).Select(strip => $"{width} /Calendar: a {strip[2]}px strip leaves its day ({strip[0]}, {strip[1]})."));

                    // The media-type and status tabs are not covered by the tools beside them.
                    foreach (var path in new[] { "/", "/Requests" })
                    {
                        await page.GotoAsync(path);
                        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                        var covered = await page.EvaluateAsync<string[]>(CoveredTabs);
                        problems.AddRange(covered.Select(item => $"{width} {path}: {item}."));
                        if (width == 390) await Evidence.SaveAsync(page, "phone-geometry-390" + (path == "/" ? "-home" : path.Replace('/', '-')));
                    }
                }

                // The desktop keeps its sizes: 40px fields, the 37px toolbar select, card buttons side by side.
                await page.SetViewportSizeAsync(1440, 900);
                await page.GotoAsync("/Settings");
                var desktopFields = await page.EvaluateAsync<JsonElement>(Fields);
                problems.AddRange(desktopFields.EnumerateArray().Where(field => field.GetProperty("frame").GetDouble() >= 42).Select(field => $"1440 /Settings {field.GetProperty("name").GetString()}: frame {field.GetProperty("frame").GetDouble()}, a phone's height."));
                problems.AddRange((await page.EvaluateAsync<double[]>(CardButtonGaps)).Where(gap => gap != 0).Select(gap => $"1440 /Settings: card buttons {gap}px apart, not 0."));
                await Evidence.SaveAsync(page, "phone-geometry-1440-settings");
                await page.GotoAsync("/Admin/Works");
                var sort = (await page.Locator(".choice-select__control.ui-dropdown__button").First.BoundingBoxAsync())!;
                if (sort.Height != 37) problems.Add($"1440 /Admin/Works: the sort select is {sort.Height}px high, not 37.");
                await page.GotoAsync("/Admin/Jobs");
                await Evidence.SaveAsync(page, "phone-geometry-1440-jobs");

                // The Reader's settings close on Escape after a click on the panel's own text, which leaves the focus outside the reader.
                await page.GotoAsync("/Read/1");
                await page.WaitForFunctionAsync("() => document.querySelector('.reader')?.dataset.bookReady === 'true'", null, new() { Timeout = 30_000 });
                var settings = page.Locator("[data-panel=settings]");
                await page.Locator("[data-action=settings]").ClickAsync();
                await Assertions.Expect(settings).ToBeVisibleAsync();
                var panel = (await settings.BoundingBoxAsync())!;
                await page.Mouse.ClickAsync(panel.X + panel.Width / 2, panel.Y + 12);
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(settings).ToBeHiddenAsync();
                await Evidence.SaveAsync(page, "phone-geometry-1440-reader-escape");

                Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
            }
            catch
            {
                await Evidence.SaveAsync(page, "phone-geometry");
                throw;
            }
        }

        // What the screen shows (a full-page picture would unpin the floating pill and the sheet).
        private static Task ScreenAsync(IPage page, string name) =>
            page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(JularrInstance.Repository, "TestResults", "e2e", name + ".png") });

        // The open sheet once its slide-in has ended: on the screen from its top to the end of its footer.
        private static async Task SheetFitsAsync(IPage page, List<string> problems, string name, int height)
        {
            var drawer = page.Locator("dialog.drawer[open]");
            await Assertions.Expect(drawer).ToBeVisibleAsync();
            await drawer.EvaluateAsync("drawer => Promise.all(drawer.getAnimations().map(animation => animation.finished))");
            var box = (await drawer.BoundingBoxAsync())!;
            var footer = await drawer.Locator(".drawer__footer").BoundingBoxAsync();
            var end = footer is null ? box.Y + box.Height : footer.Y + footer.Height;
            Evidence.Log($"[phone-geometry] {name}: sheet {box.Y}-{box.Y + box.Height}, footer ends at {end}, screen {height}.");
            if (box.Y < 0 || box.Y + box.Height > height + 0.5 || end > height + 0.5)
            {
                problems.Add($"{name}: the sheet spans {box.Y}-{box.Y + box.Height}, its footer ends at {end}, the screen at {height}.");
            }
        }
    }
}
