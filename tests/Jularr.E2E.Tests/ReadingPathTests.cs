using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // A novel, a manga volume and a light novel volume from their metadata providers through request, download and import to reading on and continuing after signing in anew.
    [TestClass]
    public sealed class ReadingPathTests
    {
        [TestMethod]
        [Timeout(180_000)]
        public async Task LocalEpub_ReaderOpening_ReturnsContent()
        {
            using var folders = new Folders("Books");
            Steps.WriteEpub(Path.Combine(folders.Library, "Books", "reader-opening.epub"), "Reader opening", "Local fixture");
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            try
            {
                await AddBookAsync(page, folders, "Reader opening");
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var response = await page.GotoAsync("/Read/1", new() { Timeout = 30_000 });
                Assert.AreEqual(200, response!.Status);
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter One", new() { Timeout = 30_000 });
                Evidence.Log("[reader-opening] Local EPUB returned content in " + System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F0") + " ms.");
                await Evidence.SaveAsync(page, "reader-opening-only");
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookReady === 'true' && document.querySelectorAll('.reader__page-print').length >= 2");
                var prints = await VisiblePagePrintsAsync(page);
                Assert.AreEqual(2, prints.Length, "The wide Reader must expose two printed pages.");
                Assert.AreNotEqual(prints[0], prints[1], "The spread must not duplicate the first column.");
                Assert.AreEqual(0, await page.Locator(".reader__page-print [data-b]").CountAsync(), "Decorative faces must not retain whole chapter blocks.");
                Assert.IsTrue(await page.Locator(".reader__page-print").EvaluateAllAsync<bool>("prints => prints.every(p => Number(p.dataset.printRuns) <= 512 && p.querySelectorAll('*').length <= 1024)"));
                Assert.IsTrue(await page.Locator(".reader__footer").EvaluateAsync<bool>("el => getComputedStyle(el).borderTopWidth === '0px' && getComputedStyle(el).boxShadow === 'none'"));
                Assert.IsTrue(await page.Locator(".reader__top").EvaluateAsync<bool>("el => getComputedStyle(el).borderBottomWidth === '0px'"));
                var transport = page.Locator("[data-role=seek] input[type=range]");
                Assert.AreEqual("none", await transport.EvaluateAsync<string>("el => getComputedStyle(el).appearance"));
                Assert.IsTrue(await transport.EvaluateAsync<bool>("el => getComputedStyle(el).accentColor !== 'auto' && el.style.getPropertyValue('--range-progress').endsWith('%')"));
                await transport.FocusAsync();
                Assert.AreEqual("none", await page.Locator("[data-role=seek] .ui-field__control").EvaluateAsync<string>("el => getComputedStyle(el).outlineStyle"));
                await transport.EvaluateAsync("el => el.blur()");
                await Evidence.SaveAsync(page, "reader-distinct-pages");
                await page.SetViewportSizeAsync(390, 844);
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.columns === '1'");
                await Evidence.SaveAsync(page, "reader-mockup-mobile-prose");
                await page.SetViewportSizeAsync(1280, 720);
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.columns === '2'");
                await AssertReaderViewsAsync(page);
                var viewSeek = page.GetByRole(AriaRole.Slider, new() { Name = "Position in this chapter", Exact = true });
                await viewSeek.FocusAsync();
                await page.Keyboard.PressAsync("End");
                await page.WaitForFunctionAsync("() => { const r = document.querySelector('.reader'); return Number(r.dataset.page) + Number(r.dataset.columns) > Number(r.dataset.pageCount); }");
                await page.Keyboard.PressAsync("Home");
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.page === '1'");
                // Keep a real layout callback pending while committing a newer seek.
                await page.EvaluateAsync("""
                    () => {
                        document.fonts.dispatchEvent(new Event('loadingdone'));
                        const seek = document.querySelector('[data-role=seek] input');
                        seek.value = seek.max;
                        seek.dispatchEvent(new Event('change', {bubbles:true}));
                    }
                    """);
                await page.WaitForFunctionAsync("() => { const r = document.querySelector('.reader'); return Number(r.dataset.page) + Number(r.dataset.columns) > Number(r.dataset.pageCount); }");
                await viewSeek.FocusAsync();
                await page.Keyboard.PressAsync("Home");
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.page === '1'");
                var canvasBeforePopover = (await page.Locator(".reader__canvas").BoundingBoxAsync())!;
                var appearance = page.Locator(".reader__tools [data-reader-open-settings=text]");
                await appearance.ClickAsync();
                await Assertions.Expect(page.Locator("[data-reader-group=text]")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator("[data-reader-group=paper]")).ToBeHiddenAsync();
                await page.Locator("[data-reader-close=settings]").ClickAsync();
                await Assertions.Expect(appearance).ToBeFocusedAsync();
                await page.Locator(".reader__tools [data-reader-open-settings=paper]").ClickAsync();
                await Assertions.Expect(page.GetByLabel("Paper colour", new() { Exact = true })).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "reader-mockup-paper-popover");
                await page.Keyboard.PressAsync("Escape");
                var canvasAfterPopover = (await page.Locator(".reader__canvas").BoundingBoxAsync())!;
                Assert.AreEqual(canvasBeforePopover.Width, canvasAfterPopover.Width);
                Assert.AreEqual(canvasBeforePopover.Height, canvasAfterPopover.Height);
                var headerBox = (await page.Locator(".reader__top").BoundingBoxAsync())!;
                var turnBox = (await page.Locator("[data-turn='1']").BoundingBoxAsync())!;
                Assert.IsTrue(turnBox.Y >= canvasBeforePopover.Y + canvasBeforePopover.Height, "Page buttons belong in the lower transport, not over the book.");
                Assert.IsTrue((await appearance.BoundingBoxAsync())!.Y < headerBox.Y + headerBox.Height);
                Assert.IsTrue((await page.Locator("[data-action=contents]").BoundingBoxAsync())!.Y >= canvasBeforePopover.Y + canvasBeforePopover.Height);
                var initialPage = int.Parse((await page.Locator(".reader").GetAttributeAsync("data-page"))!);
                var sheet = (await page.Locator("[data-book-sheet=right]").BoundingBoxAsync())!;
                await page.Mouse.MoveAsync(sheet.X + sheet.Width - 16, sheet.Y + sheet.Height / 2);
                await page.Mouse.DownAsync();
                await page.Mouse.MoveAsync(sheet.X + sheet.Width * .7f, sheet.Y + sheet.Height / 2, new() { Steps = 8 });
                await page.WaitForFunctionAsync("() => { const r = document.querySelector('.reader'); return r.dataset.bookState === 'holding' && r.dataset.bookPendingFrame === 'false'; }");
                Assert.AreEqual(initialPage.ToString(), await page.Locator(".reader").GetAttributeAsync("data-page"), "A held sheet does not commit reading progress.");
                await Evidence.SaveAsync(page, "reader-real-held-leaf");
                await page.Mouse.UpAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookState === 'idle'");
                Assert.AreEqual(initialPage.ToString(), await page.Locator(".reader").GetAttributeAsync("data-page"));
                var concurrent = await page.EvaluateAsync<int>("""
                    () => {
                        const next = document.querySelector('[data-turn="1"]'), previous = document.querySelector('[data-turn="-1"]');
                        next.click(); next.click(); next.click();
                        const count = Number(document.querySelector('.reader').dataset.bookActiveLeaves);
                        previous.click(); previous.click();
                        return count;
                    }
                    """);
                Assert.AreEqual(3, concurrent, "Quick input must move bounded concurrent leaves, not queue every page.");
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookState === 'idle'");
                Assert.AreEqual((initialPage + 2).ToString(), await page.Locator(".reader").GetAttributeAsync("data-page"), "Reversal must finish on the requested spread without an extra commit.");
                Assert.AreEqual("0", await page.Locator(".reader").GetAttributeAsync("data-book-active-leaves"));
                Assert.AreEqual("false", await page.Locator(".reader").GetAttributeAsync("data-book-pending-frame"));
                var seek = page.GetByRole(AriaRole.Slider, new() { Name = "Position in this chapter", Exact = true });
                await page.Locator("[data-action=settings]").ClickAsync();
                await page.Locator("[data-reader-group=text] > summary").ClickAsync();
                var fontSize = page.GetByRole(AriaRole.Slider, new() { Name = "Text size (px)", Exact = true });
                await fontSize.FocusAsync(); await page.Keyboard.PressAsync("End");
                await page.Locator("[data-reader-close=settings]").ClickAsync();
                await seek.FocusAsync(); await page.Keyboard.PressAsync("Home");
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.page === '1'");
                var pageColumns = int.Parse((await page.Locator(".reader").GetAttributeAsync("data-columns"))!);
                Assert.IsTrue(int.Parse((await page.Locator(".reader").GetAttributeAsync("data-page-count"))!) > 12 * pageColumns);
                Assert.IsTrue(await page.EvaluateAsync<bool>("""
                    () => { for(let i=0;i<12;i++) document.querySelector('[data-turn="1"]').click();
                        const r=document.querySelector('.reader'); return r.dataset.bookBundled === 'true' && Number(r.dataset.bookActiveLeaves) <= 3; }
                    """), "Long skips must compress into a bounded paper packet.");
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookState === 'idle'");
                Assert.AreEqual((1 + 12 * pageColumns).ToString(), await page.Locator(".reader").GetAttributeAsync("data-page"));
                await seek.FocusAsync();
                await page.Keyboard.PressAsync("End");
                await page.WaitForFunctionAsync("() => { const r = document.querySelector('.reader'); return r.dataset.bookState === 'idle' && Number(r.dataset.page) + Number(r.dataset.columns) > Number(r.dataset.pageCount); }");
                prints = await VisiblePagePrintsAsync(page);
                Assert.IsTrue(prints.Any(text => text.Contains("Paragraph 60", StringComparison.Ordinal)), "The last printed page must show the chapter's final paragraph.");
                await Evidence.SaveAsync(page, "reader-last-page-print");
                await page.Locator("[data-action=settings]").ClickAsync();
                await fontSize.FocusAsync(); await page.Keyboard.PressAsync("Home");
                for (var step = 0; step < 6; step++) await page.Keyboard.PressAsync("ArrowRight");
                await page.Locator("[data-reader-close=settings]").ClickAsync();
                // The same bounded print must retain inline fonts, native markers,
                // table paint and images, without duplicating the chapter's DOM.
                await page.Locator("[data-role=content]").EvaluateAsync("""
                    source => {
                        source.replaceChildren();
                        const paragraph = document.createElement('p');
                        paragraph.append('Distinct ', Object.assign(document.createElement('strong'), {textContent:'bold print'}), ' and ', Object.assign(document.createElement('em'), {textContent:'italic print'}), '.');
                        const list = document.createElement('ol'); list.start = 7;
                        list.append(Object.assign(document.createElement('li'), {textContent:'Seventh item'}));
                        const table = document.createElement('table'); const row = table.insertRow(); row.insertCell().textContent = 'Visible cell';
                        const image = new Image(); image.src = 'data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="90" height="60"><rect width="90" height="60" fill="#84b6cb"/></svg>');
                        source.append(paragraph,list,table,image);
                    }
                    """);
                await page.WaitForFunctionAsync("() => [...document.querySelectorAll('.reader__page-print')].some(p => p.textContent.includes('bold print') && p.querySelector('img')?.complete)");
                Assert.IsTrue(await page.Locator(".reader__page-print").EvaluateAllAsync<bool>("prints => prints.some(p => [...p.querySelectorAll('span')].some(s => s.textContent === 'bold print' && Number(s.style.fontWeight) >= 600))"));
                Assert.IsTrue(await page.Locator(".reader__page-print li").EvaluateAllAsync<bool>("items => items.some(item => item.value === 7)"));
                await Evidence.SaveAsync(page, "reader-bounded-formatting");
                await page.SetViewportSizeAsync(390, 844);
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.columns === '1'");
                var mobileCanvas = (await page.Locator(".reader__canvas").BoundingBoxAsync())!;
                foreach (var selector in new[] { "[data-action=contents]", "[data-speech-toggle]", ".reader__tools [data-reader-open-settings=text]", "[data-action=more]" })
                {
                    Assert.IsTrue((await page.Locator(selector).BoundingBoxAsync())!.Y >= mobileCanvas.Y + mobileCanvas.Height, "Mobile dock action must be below the book: " + selector);
                }
                Assert.IsTrue((await page.Locator("[data-reader-select]").BoundingBoxAsync())!.Y < mobileCanvas.Y);
                Assert.AreEqual(1, await page.Locator("[data-action=settings]").CountAsync());
                Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
                await Evidence.SaveAsync(page, "reader-mockup-mobile");
                await page.Locator("[data-action=more]").ClickAsync();
                await Assertions.Expect(page.Locator(".reader__more [data-reader-open-panel=notes]")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator(".reader__more [data-reader-open-panel=speech]")).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "reader-mockup-mobile-more");
                await page.Locator("[data-reader-command=fullscreen]").ClickAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.view === 'fullscreen'");
                var immersive = (await page.Locator(".reader__canvas").BoundingBoxAsync())!;
                await Assertions.Expect(page.Locator("[data-action=contents]")).ToBeHiddenAsync();
                await page.Locator(".reader__reveal--bottom").ClickAsync();
                await Assertions.Expect(page.Locator("[data-action=contents]")).ToBeVisibleAsync();
                var revealed = (await page.Locator(".reader__canvas").BoundingBoxAsync())!;
                Assert.AreEqual(immersive.Width, revealed.Width);
                Assert.AreEqual(immersive.Height, revealed.Height);
                await Evidence.SaveAsync(page, "reader-mockup-mobile-fullscreen");
                await page.Locator("[data-action=more]").ClickAsync();
                await page.Locator("[data-reader-command=fullscreen]").ClickAsync();
                await page.WaitForFunctionAsync("() => !document.fullscreenElement");
                await page.SetViewportSizeAsync(320, 740);
                Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                Evidence.Log("[reader-pages] " + exception);
                await Evidence.SaveAsync(page, "reader-opening-only");
                throw;
            }
        }

        [TestMethod]
        [Timeout(180_000)]
        public async Task LocalEpub_BookSpread_PagesAreMirrorImages()
        {
            using var folders = new Folders("Books");
            Steps.WriteEpub(Path.Combine(folders.Library, "Books", "spread.epub"), "Spread mirror", "Local fixture");
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            try
            {
                await AddBookAsync(page, folders, "Spread mirror");
                await page.GotoAsync("/Read/1", new() { Timeout = 30_000 });
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookReady === 'true' && document.querySelectorAll('.reader__page-print').length >= 2");
                var seek = page.GetByRole(AriaRole.Slider, new() { Name = "Position in this chapter", Exact = true });
                // Long running text in both columns: the two pages of a spread are mirrors of one another.
                await page.Locator("[data-role=content]").EvaluateAsync("""
                    source => {
                        source.replaceChildren();
                        const words = ['the', 'lantern', 'burned', 'quietly', 'while', 'we', 'walked', 'along', 'a', 'river', 'beneath', 'old', 'copper', 'beeches', 'and', 'spoke', 'of', 'harbours', 'windows', 'stories'];
                        const paragraph = document.createElement('p');
                        paragraph.style.marginBlock = '0';
                        paragraph.textContent = Array.from({ length: 2400 }, (_, index) => words[index * 7 % words.length]).join(' ') + '.';
                        source.append(paragraph);
                    }
                    """);
                await page.WaitForFunctionAsync("() => { const r = document.querySelector('.reader'); return r.dataset.bookThickness && Number(r.dataset.pageCount) > 6 && r.dataset.bookState === 'idle'; }");
                await seek.FocusAsync(); await page.Keyboard.PressAsync("Home");
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.page === '1'");
                // A thick book (1500 printed pages) makes the unread and read stacks differ clearly; keep that count after every re-measure.
                await page.EvaluateAsync("""
                    () => {
                        const reader = document.querySelector('.reader');
                        const thick = () => { if (reader.dataset.bookPageCount !== '1500') reader.dataset.bookPageCount = '1500'; };
                        new MutationObserver(thick).observe(reader, { attributes: true, attributeFilter: ['data-book-page-count'] });
                        thick();
                    }
                    """);
                foreach (var (width, height) in new[] { (1280, 720), (1024, 768) })
                {
                    await page.SetViewportSizeAsync(width, height);
                    await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.columns === '2'");
                    await AssertSpreadMirrorsAsync(page, $"{width}x{height} first spread");
                }
                // The theater view hides the sidebar, so even the small window shows two pages.
                await page.Locator("[data-reader-theater]").ClickAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.view === 'theater'");
                foreach (var (width, height) in new[] { (909, 561), (1024, 768), (1920, 1080) })
                {
                    await page.SetViewportSizeAsync(width, height);
                    await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.columns === '2'");
                    await AssertSpreadMirrorsAsync(page, $"{width}x{height} theater first spread");
                }
                await seek.FocusAsync(); await page.Keyboard.PressAsync("End");
                await page.WaitForFunctionAsync("() => { const r = document.querySelector('.reader'); return r.dataset.bookState === 'idle' && Number(r.dataset.page) + Number(r.dataset.columns) > Number(r.dataset.pageCount); }");
                await AssertSpreadMirrorsAsync(page, "1920x1080 theater last spread", textFills: false);
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                Evidence.Log("[reader-spread] " + exception);
                await Evidence.SaveAsync(page, "reader-spread-failed");
                throw;
            }
        }

        // Signs in, adds the library, scans it and makes the one scanned book a work.
        private static async Task AddBookAsync(IPage page, Folders folders, string title)
        {
            await Owner.SetUpAndSignInAsync(page);
            await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Books", "Book"));
            await page.GotoAsync("/Admin/Library");
            await Steps.SubmitAsync(page, Steps.FormWith(page, "Scan Books"));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                await page.GotoAsync("/Admin/Library/Unmatched");
                if (await page.Locator("form [name=Title]").CountAsync() > 0) break;
                Assert.IsTrue(DateTime.UtcNow < deadline, "The local test EPUB was not scanned.");
                await Task.Delay(500);
            }

            await page.Locator("form [name=Title]").FillAsync(title);
            await Steps.SubmitAsync(page, Steps.FormWith(page, "Create work and identify"));
        }

        // Left and right page of the spread share height, vertical position, outline, text box and page number
        // (mirrored); pagination fills both text boxes alike.
        private static async Task AssertSpreadMirrorsAsync(IPage page, string view, bool textFills = true)
        {
            await page.EvaluateAsync("() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done)))");
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookState === 'idle' && document.querySelectorAll('.reader-book__print > .reader__page-print').length === 2");
            await Evidence.SaveAsync(page, "reader-spread-" + view.Replace(' ', '-'));
            var sides = await page.EvaluateAsync<System.Text.Json.JsonElement>("""
                () => {
                    const line = parseFloat(getComputedStyle(document.querySelector('[data-role=content]')).lineHeight);
                    const measure = side => {
                        const sheet = document.querySelector(`[data-book-sheet=${side}]`), box = sheet.getBoundingClientRect();
                        const points = sheet.style.clipPath.slice(8, -1).split(',').map(point => point.trim().split(' ').map(value => parseFloat(value) / 100));
                        const ys = points.map(point => box.top + point[1] * box.height), xs = points.map(point => box.left + point[0] * box.width);
                        const print = sheet.querySelector('.reader__page-print'), printBox = print.getBoundingClientRect();
                        const text = [...print.querySelectorAll('span')].filter(span => span.textContent.trim()).map(span => span.getBoundingClientRect());
                        const firstText = text.length ? Math.min(...text.map(rect => rect.top)) : 0, lastText = text.length ? Math.max(...text.map(rect => rect.bottom)) : 0;
                        const number = print.querySelector('[data-book-number]').getBoundingClientRect();
                        const top = Math.min(...ys), bottom = Math.max(...ys);
                        return { sheetTop: box.top, sheetHeight: box.height, top, bottom, height: bottom - top, width: Math.max(...xs) - Math.min(...xs),
                            printTop: printBox.top, printHeight: printBox.height,
                            topPadding: firstText - top, textEndGap: bottom - lastText, numberGap: bottom - number.bottom,
                            numberOuter: side === 'left' ? (number.left + number.right) / 2 - Math.min(...xs) : Math.max(...xs) - (number.left + number.right) / 2,
                            columns: Number(document.querySelector('.reader').dataset.columns) };
                    };
                    return { line, left: measure('left'), right: measure('right') };
                }
                """);
            var left = sides.GetProperty("left"); var right = sides.GetProperty("right");
            var line = sides.GetProperty("line").GetDouble();
            double Value(System.Text.Json.JsonElement side, string name) => side.GetProperty(name).GetDouble();
            var differences = new List<string>();
            foreach (var name in new[] { "sheetTop", "sheetHeight", "top", "bottom", "height", "width", "printTop", "printHeight", "numberGap", "numberOuter" })
            {
                if (Math.Abs(Value(left, name) - Value(right, name)) > 1) differences.Add($"{name} {Value(left, name):F1} vs {Value(right, name):F1}");
            }

            if (textFills)
            {
                if (Math.Abs(Value(left, "topPadding") - Value(right, "topPadding")) > 1) differences.Add($"topPadding {Value(left, "topPadding"):F1} vs {Value(right, "topPadding"):F1}");
                if (Math.Abs(Value(left, "textEndGap") - Value(right, "textEndGap")) >= line) differences.Add($"textEndGap {Value(left, "textEndGap"):F1} vs {Value(right, "textEndGap"):F1} (line {line:F1})");
            }

            Evidence.Log($"[reader-spread] {view}: left {left} right {right}");
            Assert.AreEqual(0, differences.Count, $"{view}: left and right page differ: " + string.Join("; ", differences));
        }

        private static Task<string[]> VisiblePagePrintsAsync(IPage page) => page.EvaluateAsync<string[]>("""
            () => [...document.querySelectorAll('.reader-book__print > .reader__page-print')].filter(viewport => viewport.checkVisibility()).map(viewport => {
                const bounds = viewport.getBoundingClientRect();
                const walker = document.createTreeWalker(viewport, NodeFilter.SHOW_TEXT);
                const visible = [];
                while (walker.nextNode()) {
                    if (!walker.currentNode.textContent.trim()) continue;
                    const range = document.createRange(); range.selectNodeContents(walker.currentNode);
                    if ([...range.getClientRects()].some(r => r.right > bounds.left + 1 && r.left < bounds.right - 1 && r.bottom > bounds.top + 1 && r.top < bounds.bottom - 1)) visible.push(walker.currentNode.textContent.trim());
                }
                return visible.join(' ');
            }).filter(Boolean)
            """);

        [TestMethod]
        [Timeout(180_000)]
        public async Task LocalComic_BookRenderer_CoverSpreadAndMobile()
        {
            using var folders = new Folders("Books");
            Steps.WriteComic(Path.Combine(folders.Library, "Books", "reader-images.cbz"), 5,
            [
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAEAAAABgCAIAAAAip+O/AAAAjklEQVR4nO3PQRGAQBADwZODGswgGxMo4HWPZotJjYD0uodv6QO7C6AXQC+A3ivgOK9PFUAXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdA9z/AlAXQC6AXQG884AFoPnI7cFQtKAAAAABJRU5ErkJggg=="),
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAEAAAABgCAIAAAAip+O/AAAAjklEQVR4nO3PQRGAQBADwROPJkwgBRMo4HWPZotJjYD0uodv6QO7C6AXQC+A3ivgOK9PFUAXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdAF0AXQBdA9z/AlAXQC6AXQG884AGvDBc6YcFCkgAAAABJRU5ErkJggg==")
            ]);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Books", "Book"));
                await page.GotoAsync("/Admin/Library");
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Scan Books"));
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (true)
                {
                    await page.GotoAsync("/Admin/Library/Unmatched");
                    if (await page.Locator("form [name=Title]").CountAsync() > 0) break;
                    Assert.IsTrue(DateTime.UtcNow < deadline, "The local comic was not scanned.");
                    await Task.Delay(500);
                }
                await page.Locator("form [name=Title]").FillAsync("Reader images");
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Create work and identify"));
                await page.GotoAsync("/Read/1");
                await page.WaitForFunctionAsync("() => { const r=document.querySelector('.reader'); return r.dataset.bookReady === 'true' && r.dataset.columns === '1'; }");
                Assert.AreEqual(1, await page.Locator(".reader-book__print img").CountAsync());
                await page.Locator("[data-turn='1']").ClickAsync();
                await page.WaitForFunctionAsync("() => { const r=document.querySelector('.reader'); return r.dataset.bookState === 'idle' && r.dataset.page === '1' && r.dataset.columns === '2'; }");
                var sources = await page.Locator(".reader-book__print img").EvaluateAllAsync<string[]>("images => images.map(image => image.src)");
                Assert.AreEqual(2, sources.Length);
                Assert.AreNotEqual(sources[0], sources[1]);
                await Evidence.SaveAsync(page, "reader-image-spread");
                await page.SetViewportSizeAsync(390, 844);
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.columns === '1'");
                Assert.AreEqual(1, await page.Locator(".reader-book__print img").CountAsync());
                var box = (await page.Locator("[data-book-sheet=right]").BoundingBoxAsync())!;
                await page.Mouse.MoveAsync(box.X + box.Width * .75f, box.Y + box.Height / 2);
                await page.Mouse.DownAsync();
                await page.Mouse.MoveAsync(box.X + box.Width * .2f, box.Y + box.Height / 2, new() { Steps = 8 });
                await page.Mouse.UpAsync();
                await page.WaitForFunctionAsync("() => { const r=document.querySelector('.reader'); return r.dataset.bookState === 'idle' && r.dataset.page === '2'; }");
                box = (await page.Locator("[data-book-sheet=right]").BoundingBoxAsync())!;
                await page.Mouse.MoveAsync(box.X + box.Width * .25f, box.Y + box.Height / 2);
                await page.Mouse.DownAsync();
                await page.Mouse.MoveAsync(box.X + box.Width * .8f, box.Y + box.Height / 2, new() { Steps = 8 });
                await page.Mouse.UpAsync();
                await page.WaitForFunctionAsync("() => { const r=document.querySelector('.reader'); return r.dataset.bookState === 'idle' && r.dataset.page === '1'; }");
                Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
                Assert.AreEqual("false", await page.Locator(".reader").GetAttributeAsync("data-book-pending-frame"));
                await Evidence.SaveAsync(page, "reader-image-mobile");
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                Evidence.Log("[reader-images] " + exception);
                await Evidence.SaveAsync(page, "reader-image-failure"); throw;
            }
        }

        [TestMethod]
        [Timeout(180_000)]
        public async Task LocalEpub_BackgroundPages_PauseRestartAndFailWithoutBlockingReading()
        {
            using var folders = new Folders("Books");
            Steps.WriteEpub(Path.Combine(folders.Library, "Books", "background-pages.epub"), "Background pages", "Local fixture");
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            var held = new TaskCompletionSource<IRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            var reject = 0;
            await page.RouteAsync("**/api/v1/reading/documents/*/sections/1", async route =>
            {
                var first = Interlocked.Increment(ref requests) == 1;
                if (first)
                {
                    held.TrySetResult(route);
                    await release.Task;
                    try { await route.ContinueAsync(); } catch (PlaywrightException) { /* The test deliberately cancelled this request. */ }
                }
                else if (Volatile.Read(ref reject) == 1) await route.FulfillAsync(new() { Status = 403, ContentType = "application/json", Body = "{}" });
                else await route.ContinueAsync();
            });
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Books", "Book"));
                await page.GotoAsync("/Admin/Library");
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Scan Books"));
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (true)
                {
                    await page.GotoAsync("/Admin/Library/Unmatched");
                    if (await page.Locator("form [name=Title]").CountAsync() > 0) break;
                    Assert.IsTrue(DateTime.UtcNow < deadline, "The local test EPUB was not scanned.");
                    await Task.Delay(500);
                }

                await page.Locator("form [name=Title]").FillAsync("Background pages");
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Create work and identify"));
                await page.GotoAsync("/Read/1");
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter One");
                await held.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var root = page.Locator(".reader");
                var originalPages = int.Parse((await root.GetAttributeAsync("data-page-count"))!);
                Assert.IsNull(await root.GetAttributeAsync("data-book-page-count"), "Pending measurements must not invent a total.");
                var title = (await page.Locator(".reader__title").BoundingBoxAsync())!;
                await page.Mouse.MoveAsync(title.X + 5, title.Y + 5);
                await page.Mouse.DownAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookPageState === 'paused'");
                Assert.AreEqual(1, Volatile.Read(ref requests));
                Assert.AreEqual(0, await page.Locator(".reader__measurement [data-b]").CountAsync());
                await page.Mouse.UpAsync();
                release.TrySetResult();
                await page.Locator("[data-action=settings]").ClickAsync();
                await page.Locator("[data-reader-group=text] > summary").ClickAsync();
                var size = page.GetByRole(AriaRole.Slider, new() { Name = "Text size (px)", Exact = true });
                await size.FocusAsync();
                await page.Keyboard.PressAsync("End");
                await page.Locator("[data-reader-close=settings]").ClickAsync();
                await page.WaitForFunctionAsync("pages => Number(document.querySelector('.reader').dataset.pageCount) > pages", originalPages);
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookPageState === 'ready'");
                var chapterPages = int.Parse((await root.GetAttributeAsync("data-page-count"))!);
                Assert.AreEqual(chapterPages * 2, int.Parse((await root.GetAttributeAsync("data-book-page-count"))!), "The two equal fixture chapters must both be counted with the new font.");
                Assert.AreEqual("0", await root.GetAttributeAsync("data-book-page-before"));
                Assert.AreEqual(1, await page.Locator(".reader__measurement").CountAsync());
                Assert.AreEqual(0, await page.Locator(".reader__measurement [data-b]").CountAsync(), "No extra chapter DOM may remain after counting.");
                await page.Locator("[data-action=settings]").ClickAsync();
                await Evidence.SaveAsync(page, "reader-book-pages-ready");
                await page.Locator("[data-reader-close=settings]").ClickAsync();

                // A new viewport invalidates the old total; an unavailable chapter is not an estimate or an automatic retry loop.
                Volatile.Write(ref reject, 1);
                await page.SetViewportSizeAsync(390, 844);
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookPageState === 'unavailable'");
                Assert.IsNull(await root.GetAttributeAsync("data-book-page-count"));
                Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"), "The hidden measurement must not enlarge a resized viewport.");
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter One");
                await page.Locator("[data-action=more]").ClickAsync();
                await page.Locator("[data-reader-open-settings=all]").ClickAsync();
                await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Book page count is unavailable" }).ScrollIntoViewIfNeededAsync();
                await Evidence.SaveAsync(page, "reader-book-pages-unavailable");
                Assert.AreEqual(0, await page.Locator(".reader__measurement [data-b]").CountAsync());
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.Where(problem => !problem.Contains("403", StringComparison.Ordinal)).ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                Evidence.Log("[book-pages] " + exception);
                await Evidence.SaveAsync(page, "reader-book-pages-failure");
                throw;
            }
            finally { release.TrySetResult(); }
        }

        [TestMethod]
        [Timeout(600_000)]
        public async Task BookMangaAndLightNovel_FromMetadataToRequestDownloadImportReadAndContinue()
        {
            using var folders = new Folders("Books", "Manga", "Light Novels");
            var book = new FakeRelease("Jane Austen - Pride and Prejudice (epub)", "pride", 7020,
                Steps.WriteEpub(Path.Combine(folders.Fixtures, "book.epub"), FakeServices.BookTitle, "Jane Austen"));
            var volume = new FakeRelease("Example Manga v01 (2019) (Digital)", "v01", 7030, Steps.WriteComic(Path.Combine(folders.Fixtures, "volume.cbz"), 3));
            var novel = new FakeRelease("Example Novel v01 (2020) (epub)", "novel", 7020,
                Steps.WriteEpub(Path.Combine(folders.Fixtures, "novel.epub"), FakeServices.NovelTitle, "Ren Example"));
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Providers:OpenLibrary"] = fakes.OpenLibrary.ToString(),
                ["Providers:Images:covers.openlibrary.org"] = fakes.Images.ToString(),
                ["Providers:AniList"] = fakes.AniList.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Books", "Book"), new LibraryPath("Manga", "Book", "Manga"),
                    new LibraryPath("Light Novels", "Book", "Light novel"));
                await Steps.AddUsenetAsync(page, fakes, "7000,7020,7030");

                // The novel is added from Open Library on the Works page; the manga and the light novel are found on Discover in AniList.
                await Steps.ImportAndRequestAsync(page, "Book", null, FakeServices.BookTitle);
                await Steps.RequestFromCatalogAsync(page, "manga", FakeServices.MangaTitle, "anilist");
                await Steps.RequestFromCatalogAsync(page, "lightnovels", FakeServices.NovelTitle, "anilist");

                // Each search finds its release, offered just before; the chosen one is downloaded.
                fakes.Offer(book);
                await Steps.OpenSearchAsync(page, FakeServices.BookTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                await Assertions.Expect(page.Locator("tr", new() { HasText = book.Title })).ToContainTextAsync("Eligible");
                await Steps.GrabAsync(page, book);
                fakes.Offer(volume);
                await Steps.OpenSearchAsync(page, FakeServices.MangaTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                await Assertions.Expect(page.Locator("tr", new() { HasText = volume.Title })).ToContainTextAsync("Eligible");
                await Steps.GrabAsync(page, volume);
                fakes.Offer(novel);
                await Steps.OpenSearchAsync(page, FakeServices.NovelTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                await Assertions.Expect(page.Locator("tr", new() { HasText = novel.Title })).ToContainTextAsync("Eligible");
                await Steps.GrabAsync(page, novel);
                CollectionAssert.AreEqual(new[] { book, volume, novel }.Select(release => fakes.NzbAddress(release).ToString()).ToArray(), fakes.Grabbed.ToArray());

                // Both land in their own library path.
                var bookDetails = await Steps.WaitForAvailableAsync(page, FakeServices.BookTitle, "Read");
                var mangaDetails = await Steps.WaitForAvailableAsync(page, FakeServices.MangaTitle, "Read");
                var novelDetails = await Steps.WaitForAvailableAsync(page, FakeServices.NovelTitle, "Read");
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Books"), "*.epub", SearchOption.AllDirectories).Count(), "The novel was not imported once.");
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Manga"), "*.cbz", SearchOption.AllDirectories).Count(), "The volume was not imported once.");
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Light Novels"), "*.epub", SearchOption.AllDirectories).Count(), "The light novel was not imported once.");

                // Reading on saves the position in each.
                await Steps.OpenReaderAsync(page, bookDetails);
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter One");
                await AssertPaperReaderAsync(page);
                await Steps.ReadNextSectionAsync(page);
                await Steps.OpenReaderAsync(page, mangaDetails);
                await AssertReaderViewsAsync(page, images: true);
                await Steps.TurnAsync(page);
                await Steps.OpenReaderAsync(page, novelDetails);
                await Steps.ReadNextSectionAsync(page);

                // Signed out and in again, both open where reading stopped.
                await Steps.SignOutAndInAsync(page);
                await Steps.OpenReaderAsync(page, bookDetails);
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter Two");
                await Steps.OpenReaderAsync(page, mangaDetails);
                // A wide window shows two pages side by side.
                StringAssert.Matches((await page.Locator("[data-role=position]").InnerTextAsync()).Trim(), new System.Text.RegularExpressions.Regex("^2(–3)? / 3$"));
                await Steps.OpenReaderAsync(page, novelDetails);
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter Two");

                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "reading-path");
                throw;
            }
        }

        private static async Task AssertPaperReaderAsync(IPage page)
        {
            var root = page.Locator(".reader");
            await page.WaitForFunctionAsync("() => Number(document.querySelector('.reader').dataset.pageCount) > 2");
            await AssertReaderViewsAsync(page);
            var seek = page.GetByRole(AriaRole.Slider, new() { Name = "Position in this chapter", Exact = true });
            await seek.FocusAsync();
            await page.Keyboard.PressAsync("End");
            await page.WaitForFunctionAsync("() => { const r = document.querySelector('.reader'); return Number(r.dataset.page) + Number(r.dataset.columns) > Number(r.dataset.pageCount); }");
            Assert.IsTrue(await page.Locator("[data-role=content]").EvaluateAsync<bool>("""
                el => {
                    const bounds = el.getBoundingClientRect();
                    const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
                    while (walker.nextNode()) {
                        if (!walker.currentNode.textContent.trim()) continue;
                        const range = document.createRange(); range.selectNodeContents(walker.currentNode);
                        if ([...range.getClientRects()].some(r => r.right > bounds.left && r.left < bounds.right && r.bottom > bounds.top && r.top < bounds.bottom)) return true;
                    }
                    return false;
                }
                """), "The last calculated page must contain visible text, not a blank extra page.");
            await page.WaitForFunctionAsync("""
                () => [...document.querySelectorAll('.reader__page-print')].some(viewport => {
                    const bounds = viewport.getBoundingClientRect();
                    if (!bounds.width || !bounds.height) return false;
                    const walker = document.createTreeWalker(viewport, NodeFilter.SHOW_TEXT);
                    while (walker.nextNode()) {
                        if (!walker.currentNode.textContent.trim()) continue;
                        const range = document.createRange(); range.selectNodeContents(walker.currentNode);
                        if ([...range.getClientRects()].some(r => r.right > bounds.left && r.left < bounds.right && r.bottom > bounds.top && r.top < bounds.bottom)) return true;
                    }
                    return false;
                })
                """);
            await Evidence.SaveAsync(page, "reader-chapter-seek");
            await page.Keyboard.PressAsync("Home");
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.page === '1'");
            await page.Keyboard.PressAsync("ArrowRight");
            await page.WaitForFunctionAsync("() => Number(document.querySelector('.reader').dataset.page) > 1");
            await page.Keyboard.PressAsync("Home");
            await page.Locator("[data-action=settings]").ClickAsync();
            await page.Locator("[data-reader-group=text] > summary").ClickAsync();
            var size = page.GetByRole(AriaRole.Slider, new() { Name = "Text size (px)", Exact = true });
            await size.FocusAsync();
            await page.Keyboard.PressAsync("Home");
            Assert.AreEqual("14", await size.InputValueAsync());
            await page.Keyboard.PressAsync("ArrowRight");
            Assert.AreEqual("15", await size.InputValueAsync());
            Assert.AreEqual("15px", await root.EvaluateAsync<string>("el => el.style.getPropertyValue('--reader-size')"));
            await page.Keyboard.PressAsync("End");
            Assert.AreEqual("40", await size.InputValueAsync());
            await page.Keyboard.PressAsync("Home");
            for (var step = 0; step < 6; step++) await page.Keyboard.PressAsync("ArrowRight");
            Assert.AreEqual("20", await size.InputValueAsync());
            await page.GetByLabel("Font family", new() { Exact = true }).SelectOptionAsync("sans");
            StringAssert.Contains(await page.Locator("[data-role=content]").EvaluateAsync<string>("el => getComputedStyle(el).fontFamily"), "Segoe UI");
            var width = page.GetByRole(AriaRole.Slider, new() { Name = "Text width (characters)", Exact = true });
            await width.FocusAsync();
            await page.Keyboard.PressAsync("Home");
            Assert.AreEqual("40", await width.InputValueAsync());
            Assert.AreEqual("40ch", await root.EvaluateAsync<string>("el => el.style.getPropertyValue('--reader-text-width')"));
            await page.GetByLabel("Indent paragraphs", new() { Exact = true }).FocusAsync();
            await page.Keyboard.PressAsync("Space");
            Assert.IsTrue(await page.GetByLabel("Indent paragraphs", new() { Exact = true }).IsCheckedAsync());
            Assert.IsTrue(await page.Locator("[data-role=content] p + p").First.EvaluateAsync<bool>("el => parseFloat(getComputedStyle(el).textIndent) > 0"));
            await page.GetByText("Hyphenation", new() { Exact = true }).ClickAsync();
            Assert.AreEqual("none", await page.Locator("[data-role=content]").EvaluateAsync<string>("el => getComputedStyle(el).hyphens"));
            await page.GetByText("Show illustrations", new() { Exact = true }).ClickAsync();
            Assert.AreEqual("false", await root.GetAttributeAsync("data-illustrations"));
            await Evidence.SaveAsync(page, "reader-shared-sliders");
            // Restore the unchanged starting typography before the existing paging regressions.
            await page.GetByLabel("Font family", new() { Exact = true }).SelectOptionAsync("serif");
            await width.FocusAsync();
            for (var step = 0; step < 28; step++) await page.Keyboard.PressAsync("ArrowRight");
            await page.GetByText("Indent paragraphs", new() { Exact = true }).ClickAsync();
            await page.GetByText("Hyphenation", new() { Exact = true }).ClickAsync();
            await page.GetByText("Show illustrations", new() { Exact = true }).ClickAsync();
            await page.Locator("[data-reader-group=text] > summary").ClickAsync();
            await page.Locator("[data-reader-group=motion] > summary").ClickAsync();
            await page.GetByLabel("Page turn animation", new() { Exact = true }).SelectOptionAsync("on");
            await page.Locator("[data-action=settings]").ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookReady === 'true'");
            Assert.IsTrue(await page.Locator(".reader-book__turn").CountAsync() <= 3);
            var margins = await page.Locator("[data-role=content]").EvaluateAsync<double>("el => el.querySelector('p').getBoundingClientRect().left - el.getBoundingClientRect().left");
            Assert.IsTrue(margins >= 23, "Each paper page needs its own inner text margin.");
            var area = (await page.Locator(".reader-book").BoundingBoxAsync())!;
            var initial = await root.GetAttributeAsync("data-page");
            await page.Mouse.MoveAsync(area.X + area.Width - 8, area.Y + area.Height - 12);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(area.X + area.Width * .65f, area.Y + area.Height - 60, new() { Steps = 18 });
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookState === 'holding'");
            Assert.AreEqual(initial, await root.GetAttributeAsync("data-page"), "Holding a page must not save an uncommitted turn.");
            Assert.IsTrue(await page.Locator(".reader-book__fold-front").EvaluateAllAsync<bool>("items => items.some(el => el.style.clipPath.startsWith('polygon('))"));
            await page.Mouse.MoveAsync(area.X + area.Width - 8, area.Y + area.Height - 12, new() { Steps = 18 });
            await page.Mouse.UpAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookState === 'idle'");
            Assert.AreEqual(initial, await root.GetAttributeAsync("data-page"));
            await page.RunAndWaitForResponseAsync(() => page.Locator("[data-turn='1']").ClickAsync(), response => response.Request.Method == "PUT" && response.Ok);
            Assert.AreNotEqual(initial, await root.GetAttributeAsync("data-page"));
            await page.Locator("[data-reader-select]").ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookReady === 'false'");
            await page.Locator("[data-reader-select]").ClickAsync();

            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.Locator("[data-action=settings]").ClickAsync();
            await page.GetByLabel("Page turn animation", new() { Exact = true }).SelectOptionAsync("system");
            await page.Locator("[data-action=settings]").ClickAsync();
            await page.WaitForFunctionAsync("() => { const r = document.querySelector('.reader'); return r.dataset.bookReady === 'true' && r.dataset.bookActiveLeaves === '0' && r.dataset.bookPendingFrame === 'false'; }");
            await page.SetViewportSizeAsync(390, 844);
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.columns === '1'");
            Assert.IsFalse(await page.Locator(".app-sidebar").IsVisibleAsync());
            Assert.AreEqual(1, await page.Locator("[data-action=settings]").CountAsync(), "Mobile must reuse the same controls, not another toolbar copy.");
            var mobileTools = await page.Locator("[data-action=contents]").BoundingBoxAsync();
            var mobileCanvas = await page.Locator(".reader__canvas").BoundingBoxAsync();
            Assert.IsTrue(mobileTools!.Y >= mobileCanvas!.Y + mobileCanvas.Height, "Mobile tools belong below the reading surface.");
            await Evidence.SaveAsync(page, "reader-mobile-tools");
            await page.Locator("[data-action=more]").ClickAsync();
            await page.Locator("[data-reader-command=fullscreen]").ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.view === 'fullscreen'");
            var mobileFullscreenCanvas = await page.Locator(".reader__canvas").BoundingBoxAsync();
            await Assertions.Expect(page.Locator("[data-action=contents]")).ToBeHiddenAsync();
            await page.Locator(".reader__reveal--bottom").ClickAsync();
            await Assertions.Expect(page.Locator(".reader__identity")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("[data-action=contents]")).ToBeVisibleAsync();
            var revealedMobileCanvas = await page.Locator(".reader__canvas").BoundingBoxAsync();
            Assert.AreEqual(mobileFullscreenCanvas!.Width, revealedMobileCanvas!.Width);
            Assert.AreEqual(mobileFullscreenCanvas.Height, revealedMobileCanvas.Height);
            mobileTools = await page.Locator("[data-action=contents]").BoundingBoxAsync();
            var mobileProgress = await page.Locator(".reader__bottom").BoundingBoxAsync();
            Assert.IsTrue(mobileTools!.Y >= mobileProgress!.Y + mobileProgress.Height - 1, "Revealed mobile strips must not overlap.");
            await Evidence.SaveAsync(page, "reader-mobile-fullscreen-controls");
            await page.Locator("[data-action=more]").ClickAsync();
            await page.Locator("[data-reader-command=fullscreen]").ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.view === 'normal'");
            await page.Locator("[data-action=contents]").ClickAsync();
            Assert.IsTrue(await page.Locator(".reader__sidebar").IsVisibleAsync());
            var mobileContentsCanvas = await page.Locator(".reader__canvas").BoundingBoxAsync();
            Assert.AreEqual(mobileCanvas!.Width, mobileContentsCanvas!.Width);
            Assert.AreEqual(mobileCanvas.Height, mobileContentsCanvas.Height);
            await page.Keyboard.PressAsync("Escape");
            await page.Locator("[data-action=more]").ClickAsync();
            await page.Locator("[data-reader-open-settings=all]").ClickAsync();
            await Evidence.SaveAsync(page, "reader-mobile-settings");
            Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
            await page.GetByLabel("Layout", new() { Exact = true }).SelectOptionAsync("scroll");
            await page.Locator("[data-reader-group=paper] > summary").ClickAsync();
            await page.GetByLabel("Paper colour", new() { Exact = true }).FillAsync("#242424");
            await page.Locator("[data-reader-close=settings]").ClickAsync();
            Assert.AreEqual("scroll", await root.GetAttributeAsync("data-mode"));
            Assert.AreEqual("rgb(250, 250, 250)", await root.EvaluateAsync<string>("el => getComputedStyle(el.querySelector('[data-role=content]')).color"));
            Assert.IsTrue(await page.Locator("[data-role=content]").EvaluateAsync<bool>("el => el.scrollHeight > el.clientHeight"));
            await seek.FocusAsync();
            await page.Keyboard.PressAsync("End");
            Assert.IsTrue(await page.Locator("[data-role=content]").EvaluateAsync<bool>("el => el.scrollTop + el.clientHeight >= el.scrollHeight - 2"));
            await page.Keyboard.PressAsync("Home");
            await page.SetViewportSizeAsync(1280, 720);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.NoPreference });
        }

        private static async Task AssertReaderViewsAsync(IPage page, bool images = false)
        {
            await page.WaitForFunctionAsync("() => document.querySelector('.reader')?.dataset.view === 'normal'");
            var root = page.Locator(".reader");
            var theater = page.Locator("[data-reader-theater]");
            var fullscreen = page.Locator("[data-reader-fullscreen]");
            var canvas = page.Locator(".reader__canvas");
            Assert.AreEqual("normal", await root.GetAttributeAsync("data-view"));
            Assert.IsTrue(await page.Locator(".app-sidebar").IsVisibleAsync());
            Assert.IsFalse(await page.Locator(".app-header").IsVisibleAsync());
            Assert.IsFalse(await page.Locator(".reader__sidebar").IsVisibleAsync());
            Assert.AreEqual(1, await page.Locator(".reader__top .reader__cover").CountAsync());
            Assert.AreEqual(1, await page.Locator(".reader__cover .ui-icon").CountAsync(), "Fixtures without artwork retain a deliberate cover fallback.");
            Assert.AreEqual(0, await page.Locator(".app-sidebar .reader__cover, .continue-bar, .save-pill").CountAsync());
            Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("el => el.hasAttribute('hx-boost:inherited')"), "The document renderer must not survive an in-place navigation away.");
            var initialCanvas = await canvas.BoundingBoxAsync();
            await page.Locator("[data-action=contents]").ClickAsync();
            Assert.IsTrue(await page.Locator(".reader__sidebar").IsVisibleAsync());
            var contentsCanvas = await canvas.BoundingBoxAsync();
            Assert.AreEqual(initialCanvas!.X, contentsCanvas!.X);
            Assert.AreEqual(initialCanvas.Y, contentsCanvas.Y);
            Assert.AreEqual(initialCanvas.Width, contentsCanvas.Width);
            Assert.AreEqual(initialCanvas.Height, contentsCanvas.Height, "Contents must overlay, not repaginate.");
            await Evidence.SaveAsync(page, images ? "reader-comic-contents" : "reader-contents");
            await page.Locator("[data-reader-close=contents]").ClickAsync();
            Assert.IsTrue(await page.Locator("[data-action=contents]").EvaluateAsync<bool>("el => el === document.activeElement"));
            await page.Locator("[data-action=contents]").ClickAsync();
            await page.Keyboard.PressAsync("Escape");
            Assert.IsFalse(await page.Locator(".reader__sidebar").IsVisibleAsync());
            await Evidence.SaveAsync(page, images ? "reader-comic-normal" : "reader-normal");
            await theater.ClickAsync();
            Assert.AreEqual("theater", await root.GetAttributeAsync("data-view"));
            Assert.IsFalse(await page.Locator(".app-sidebar").IsVisibleAsync());
            Assert.IsFalse(await page.Locator(".reader__sidebar").IsVisibleAsync());
            Assert.IsTrue(await page.Locator(".reader__top").IsVisibleAsync());
            Assert.IsTrue(await page.Locator(".reader__footer").IsVisibleAsync());
            await page.EvaluateAsync("() => document.documentElement.dataset.mode = 'dark'");
            await Evidence.SaveAsync(page, images ? "reader-comic-theater" : "reader-theater");
            await page.EvaluateAsync("() => document.documentElement.dataset.mode = 'system'");
            await root.EvaluateAsync("el => { el.requestFullscreen = async () => { throw new Error('Fullscreen unavailable in test'); }; }");
            await fullscreen.ClickAsync();
            Assert.IsTrue(await page.Locator("[data-role=fullscreen-error]").IsVisibleAsync());
            Assert.AreEqual("theater", await root.GetAttributeAsync("data-view"));
            await root.EvaluateAsync("el => { delete el.requestFullscreen; }");
            await fullscreen.ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.view === 'fullscreen'");
            Assert.IsFalse(await page.Locator(".reader__top").IsVisibleAsync());
            Assert.IsTrue(await page.Locator(".reader__footer").EvaluateAsync<bool>("el => el.inert"));
            var before = await canvas.BoundingBoxAsync();
            await Evidence.SaveAsync(page, images ? "reader-comic-fullscreen" : "reader-fullscreen");
            await page.Locator(".reader__reveal--bottom").ClickAsync();
            Assert.IsTrue(await page.Locator(".reader__footer").IsVisibleAsync());
            Assert.IsFalse(await page.Locator(".reader__footer").EvaluateAsync<bool>("el => el.inert"));
            var after = await canvas.BoundingBoxAsync();
            Assert.AreEqual(before!.X, after!.X);
            Assert.AreEqual(before.Y, after.Y);
            Assert.AreEqual(before.Width, after.Width);
            Assert.AreEqual(before.Height, after.Height, "Revealing chrome must not repaginate the book.");
            await page.Locator("[data-action=settings]").ClickAsync();
            Assert.AreEqual(images ? 3 : 4, await page.Locator("[data-panel=settings] .reader__settings-group").CountAsync());
            await page.Locator("[data-reader-group=paper] > summary").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            Assert.IsTrue(await page.GetByLabel("Paper colour", new() { Exact = true }).IsVisibleAsync());
            await page.Keyboard.PressAsync("Enter");
            await page.Keyboard.PressAsync("Escape");
            Assert.IsFalse(await page.Locator("[data-panel=settings]").IsVisibleAsync());
            await page.Locator(".reader__reveal--top").ClickAsync();
            Assert.AreEqual("hidden", await root.GetAttributeAsync("data-chrome"));
            await page.Locator(".reader__reveal--bottom").ClickAsync();
            await fullscreen.ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.view === 'theater'");
            await theater.ClickAsync();
            Assert.AreEqual("normal", await root.GetAttributeAsync("data-view"));
            Assert.IsFalse(await page.Locator(".reader__sidebar").IsVisibleAsync());
            Assert.IsTrue(await page.Locator(".app-sidebar").IsVisibleAsync());
            if (images)
            {
                var seek = page.GetByRole(AriaRole.Slider, new() { Name = "Page position", Exact = true });
                await seek.FocusAsync();
                await page.Keyboard.PressAsync("End");
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.page === '2'");
                await Assertions.Expect(page.Locator("[data-role=position]")).ToContainTextAsync("3 / 3");
                await Evidence.SaveAsync(page, "reader-comic-seek");
                await page.Keyboard.PressAsync("Home");
                await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.page === '0'");
            }
        }

        [TestMethod]
        [Timeout(600_000)]
        public async Task PdfBook_FromRequestDownloadAndImportReadsReflowedAndContinues()
        {
            using var folders = new Folders("Books");
            // Chapters long enough to scroll through, each paragraph a few lines with a blank line after.
            string[] Text(int paragraphs) => [.. Enumerable.Range(1, paragraphs).SelectMany(number => new[] { "Paragraph " + number + " runs across the page like the text of any printed book",
                "does, line after line, until it ends here.", "" })];
            var pdf = new FakeRelease("Jane Austen - Pride and Prejudice (pdf)", "pride-pdf", 7020, Steps.WritePdf(Path.Combine(folders.Fixtures, "book.pdf"),
                ["Chapter One", "", "", "It is a truth universally acknowledged, that a single man in possession", "of a good fortune, must be in want of a wife.", "", .. Text(13)],
                [.. Text(16)],
                ["Chapter Two", "", "", "Mr. Bennet was among the earliest of those who waited on Mr. Bingley.", "", .. Text(13)],
                [.. Text(16)]));
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Providers:OpenLibrary"] = fakes.OpenLibrary.ToString(),
                ["Providers:Images:covers.openlibrary.org"] = fakes.Images.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Books", "Book"));
                await Steps.AddUsenetAsync(page, fakes, "7000,7020");
                await Steps.ImportAndRequestAsync(page, "Book", null, FakeServices.BookTitle);

                // The PDF release is found, grabbed and imported like an EPUB.
                fakes.Offer(pdf);
                await Steps.OpenSearchAsync(page, FakeServices.BookTitle);
                await Steps.SubmitAsync(page, Steps.FormWith(page, "Start search"));
                await Steps.GrabAsync(page, pdf);
                var details = await Steps.WaitForAvailableAsync(page, FakeServices.BookTitle, "Read");
                Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(folders.Library, "Books"), "*.pdf", SearchOption.AllDirectories).Count(), "The PDF was not imported once.");

                // The reader reflows it by chapter, keeps the original at hand and continues where reading stopped.
                await Steps.OpenReaderAsync(page, details);
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("must be in want of a wife.");
                await page.Locator("[data-action=more]").ClickAsync();
                var download = page.Locator("[data-panel=more] [data-action=download]");
                await download.ClickAsync();
                Assert.AreEqual(1, await page.GetByRole(AriaRole.Link, new() { Name = "Original PDF" }).CountAsync());
                var epub = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Link, new() { Name = "EPUB", Exact = true }).ClickAsync());
                Assert.AreEqual(".epub", Path.GetExtension(epub.SuggestedFilename));
                await page.Keyboard.PressAsync("Escape");
                await Steps.ReadNextSectionAsync(page);
                await Steps.SignOutAndInAsync(page);
                await Steps.OpenReaderAsync(page, details);
                await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter Two");

                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "reading-pdf");
                throw;
            }
        }
    }
}
