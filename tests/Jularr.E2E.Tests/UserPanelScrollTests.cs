using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class UserPanelScrollTests
    {
        [TestMethod]
        [Timeout(240_000)]
        public async Task ReaderNotes_AppendWithoutOpeningBook_DeleteAndKeepNativeLinks()
        {
            using var folders = new Folders("Books");
            Steps.WriteEpub(Path.Combine(folders.Library, "Books", "notes.epub"), "Reader notes", "Local fixture");
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await ReadingPathTests.AddBookAsync(page, folders, "Reader notes");
            var asset = (long)(await jularr.ScalarAsync("SELECT \"Id\" FROM \"MediaAssets\" ORDER BY \"Id\" LIMIT 1"))!;
            await jularr.RunSqlAsync("""
                INSERT INTO "ReadingAnnotations" ("ProfileId", "MediaAssetId", "SectionIndex", "StartBlock", "StartOffset", "EndBlock", "EndOffset", "Quote", "HighlightColorTypeId")
                SELECT p."Id", {{asset}}, 1, 0, 0, 0, 1, 'Note ' || lpad(n::text, 3, '0'), 1 FROM "Profiles" p CROSS JOIN generate_series(1, 60) n;
                """.Replace("{{asset}}", asset.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await page.GotoAsync("/Read/" + asset + "?s=0");
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter One");
            await page.WaitForFunctionAsync("() => document.querySelector('.reader').dataset.bookReady === 'true'");
            var content = await page.Locator("[data-role=content]").TextContentAsync();
            var lastRead = await jularr.ScalarAsync("SELECT \"LastReadAt\" FROM \"ReadingDocuments\" LIMIT 1");
            await Assertions.Expect(page.Locator(".reader__notes > li")).ToHaveCountAsync(48);
            var tool = page.Locator("[data-overflow=notes]");
            if (await tool.IsVisibleAsync()) await tool.ClickAsync();
            else { await page.Locator("[data-action=more]").ClickAsync(); await page.Locator("[data-overflow-for=notes]").ClickAsync(); }
            var panel = page.Locator("[data-panel=notes]");
            await Assertions.Expect(panel).ToBeVisibleAsync();
            var fragment = page.WaitForResponseAsync(r => r.Url.Contains("handler=Notes", StringComparison.Ordinal));
            await panel.EvaluateAsync("p => p.scrollTop = p.scrollHeight");
            Assert.AreEqual(200, (await fragment).Status);
            await Assertions.Expect(page.Locator(".reader__notes > li")).ToHaveCountAsync(60);
            Assert.AreEqual(content, await page.Locator("[data-role=content]").TextContentAsync());
            Assert.AreEqual(lastRead, await jularr.ScalarAsync("SELECT \"LastReadAt\" FROM \"ReadingDocuments\" LIMIT 1"));
            Assert.AreEqual(60, await page.Locator(".reader__notes > li").EvaluateAllAsync<int>("rows => new Set(rows.map(r => r.dataset.listKey)).size"));
            await Evidence.SaveAsync(page, "reader-notes-appended");
            await page.Locator(".reader__notes > li").Last.Locator("button[type=submit]").ClickAsync();
            await Assertions.Expect(page.Locator("[data-role=content]")).ToContainTextAsync("Chapter One");
            Assert.AreEqual(59L, await jularr.ScalarAsync("SELECT count(*) FROM \"ReadingAnnotations\""));

            await using var plain = await session.Context.Browser!.NewContextAsync(new() { BaseURL = jularr.Address.ToString(), JavaScriptEnabled = false, StorageState = await session.Context.StorageStateAsync() });
            var bare = await plain.NewPageAsync();
            await bare.GotoAsync("/Read/" + asset + "?s=0&n=2");
            await Assertions.Expect(bare.Locator(".reader__notes > li")).ToHaveCountAsync(11);
            await bare.Locator(".reader__note-quote").First.ClickAsync();
            await Assertions.Expect(bare.Locator("[data-role=content]")).ToContainTextAsync("Chapter Two");
        }

        [TestMethod]
        [Timeout(240_000)]
        public async Task DetailsFiles_AppendGroupedInventoryAndKeepNativePaging()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                DO $$ DECLARE mount bigint; root bigint; edition bigint; version bigint; unit bigint; asset bigint; n integer;
                BEGIN
                    INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") VALUES ('Files', 3, 'C:\files-test', FALSE) RETURNING "Id" INTO mount;
                    INSERT INTO "LibraryRoots" ("Name", "MediaTypeId", "StorageMountId", "RelativePath") VALUES ('Series', 2, mount, 'series') RETURNING "Id" INTO root;
                    INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (9000, 2, 'Many files');
                    INSERT INTO "WorkEditions" ("WorkId") VALUES (9000) RETURNING "Id" INTO edition;
                    INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId") VALUES (9000, edition) RETURNING "Id" INTO version;
                    INSERT INTO "WorkUnitGroups" ("WorkId", "Name", "FirstNumber", "LastNumber") VALUES (9000, 'First arc', 1, 60), (9000, 'Second arc', 61, 144);
                    FOR n IN 1..144 LOOP
                        INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number", "Title") VALUES (9000, 2, n, 'Episode ' || n) RETURNING "Id" INTO unit;
                        INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "WorkUnitId", "MediaAssetTypeId") VALUES (9000, version, unit, 1) RETURNING "Id" INTO asset;
                        INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "MediaAssetId", "LastSeenAt") VALUES (root, n || '.mkv', 100, now(), asset, now());
                    END LOOP;
                END $$;
                """);
            await page.GotoAsync("/Details/9000");
            await Assertions.Expect(page.Locator(".details-panel tbody tr")).ToHaveCountAsync(5);
            await page.GotoAsync("/Details/9000?tab=files");
            var rows = page.Locator(".details-file-items tbody tr");
            await Assertions.Expect(rows).ToHaveCountAsync(48);
            await page.Locator("[data-infinite-next]").ScrollIntoViewIfNeededAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(96);
            await page.Locator("[data-infinite-next]").ScrollIntoViewIfNeededAsync();
            await Assertions.Expect(rows).ToHaveCountAsync(144);
            await page.Locator("[data-infinite-next]").ScrollIntoViewIfNeededAsync();
            await Assertions.Expect(page.Locator("[data-infinite-next]")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".details-file-items h3").Filter(new() { HasText = "First arc" }).First).ToBeVisibleAsync();
            Assert.IsTrue(await page.Locator(".details-file-items h3").Filter(new() { HasText = "Second arc" }).CountAsync() > 0);
            var links = await rows.Locator("a[href^='/Player/']").EvaluateAllAsync<string[]>("links => links.map(a => a.getAttribute('href'))");
            Assert.AreEqual(144, links.Distinct().Count());
            await Evidence.SaveAsync(page, "details-files-appended");

            await using var plain = await session.Context.Browser!.NewContextAsync(new() { BaseURL = jularr.Address.ToString(), JavaScriptEnabled = false, StorageState = await session.Context.StorageStateAsync() });
            var bare = await plain.NewPageAsync();
            await bare.GotoAsync("/Details/9000?tab=files");
            await bare.Locator("[data-infinite-next]").ClickAsync();
            await Assertions.Expect(bare.Locator(".details-file-items tbody tr")).ToHaveCountAsync(48);
            StringAssert.Contains(bare.Url, "p=2");
        }
    }
}
