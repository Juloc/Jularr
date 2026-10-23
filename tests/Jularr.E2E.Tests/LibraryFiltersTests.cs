using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class LibraryFiltersTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Library_FiltersAppendRetryAndRestoreAfterBack_KeepNativePaging()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                DO $$ DECLARE mount bigint; root bigint; pid bigint; aid bigint;
                BEGIN
                    SELECT "Id" INTO pid FROM "Profiles" ORDER BY "Id" LIMIT 1;
                    SELECT "Id" INTO aid FROM "Accounts" ORDER BY "Id" LIMIT 1;
                    INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") VALUES ('Library filters', 3, 'C:\library-filter-test', FALSE) RETURNING "Id" INTO mount;
                    INSERT INTO "LibraryRoots" ("Name", "MediaTypeId", "StorageMountId", "RelativePath") VALUES ('Films', 1, mount, 'films') RETURNING "Id" INTO root;
                    INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear")
                    SELECT n, 1, 'Film ' || lpad(n::text, 3, '0'), 2024 FROM generate_series(1, 122) AS n;
                    INSERT INTO "WorkEditions" ("WorkId") SELECT "Id" FROM "Works";
                    INSERT INTO "WorkVersions" ("Id", "WorkId", "WorkEditionId") SELECT "WorkId", "WorkId", "Id" FROM "WorkEditions";
                    INSERT INTO "MediaAssets" ("Id", "WorkId", "WorkVersionId", "MediaAssetTypeId") SELECT "Id", "Id", "Id", 1 FROM "Works";
                    INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "MediaAssetId", "LastSeenAt")
                    SELECT root, "Id" || '.mkv', 100, now(), "Id", now() FROM "MediaAssets" WHERE "Id" <= 120;
                    INSERT INTO "MediaTracks" ("StoredFileId", "StreamIndex", "MediaTrackTypeId", "Language", "IsDefault", "IsForced")
                    SELECT "Id", 1, 2, 'eng', TRUE, FALSE FROM "StoredFiles";
                    INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") SELECT "Id", 'Genres', 'Drama', TRUE FROM "Works";
                    INSERT INTO "AcquisitionRequests" ("WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId") VALUES (121, pid, aid, 1), (122, NULL, aid, 1);
                END $$;
                """);
            try
            {
                await page.GotoAsync("/Library?sort=Title");
                var cards = page.Locator(".library-grid .media-card");
                await Assertions.Expect(cards).ToHaveCountAsync(48);
                Assert.IsFalse((await cards.AllTextContentsAsync()).Any(text => text.Contains("Film 121", StringComparison.Ordinal)));
                // Back also works after a direct visit, before any boosted form created a history entry.
                await page.Locator("[data-infinite-list]").ScrollIntoViewIfNeededAsync();
                await Assertions.Expect(cards).ToHaveCountAsync(96);
                await cards.Nth(70).GetByRole(AriaRole.Button, new() { Name = "Add to favorites", Exact = true }).ClickAsync();
                await Assertions.Expect(cards).ToHaveCountAsync(96);
                await Assertions.Expect(cards.Nth(70).GetByRole(AriaRole.Button, new() { Name = "Remove from favorites", Exact = true })).ToBeVisibleAsync();
                await cards.Nth(70).Locator(".media-card__link").ClickAsync();
                await page.WaitForURLAsync("**/Details/**");
                await page.GoBackAsync();
                await Assertions.Expect(cards).ToHaveCountAsync(96);
                await page.GotoAsync("/Library?sort=Title");
                await Assertions.Expect(cards).ToHaveCountAsync(48);
                await page.GetByRole(AriaRole.Button, new() { Name = "Open filter", Exact = true }).ClickAsync();
                await page.Locator("#library-filter [name=genre][value=Drama]").CheckAsync();
                await page.Locator("#library-filter [name=audio][value=eng]").CheckAsync();
                await page.Locator("#library-filter button[type=submit]").ClickAsync();
                await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("genre=Drama"));
                await Assertions.Expect(cards).ToHaveCountAsync(48);

                // One failed request leaves a retry link and the existing cards intact.
                var fail = true;
                await page.RouteAsync("**/Library?**", async route =>
                {
                    if (route.Request.Headers.ContainsKey("x-jularr-infinite") && fail) { fail = false; await route.AbortAsync(); }
                    else await route.FallbackAsync();
                });
                await page.Locator("[data-infinite-list]").ScrollIntoViewIfNeededAsync();
                await Assertions.Expect(page.Locator("[data-infinite-status]")).ToContainTextAsync("Try again");
                await Assertions.Expect(cards).ToHaveCountAsync(48);
                await page.Locator("[data-infinite-next]").ClickAsync();
                await Assertions.Expect(cards).ToHaveCountAsync(96);
                var address = page.Url;
                await cards.Nth(70).Locator(".media-card__link").ClickAsync();
                await page.WaitForURLAsync("**/Details/**");
                await page.GoBackAsync();
                await Assertions.Expect(page).ToHaveURLAsync(address);
                await Assertions.Expect(cards).ToHaveCountAsync(96);
                Assert.IsTrue(await page.EvaluateAsync<bool>("scrollY > 500"), "Back lost the scroll position.");
                await page.Locator("[data-infinite-list]").ScrollIntoViewIfNeededAsync();
                await Assertions.Expect(cards).ToHaveCountAsync(120);
                await Assertions.Expect(page.Locator("[data-infinite-next]")).ToHaveCountAsync(0);
                var titles = await cards.Locator(".media-card__link").AllTextContentsAsync();
                Assert.AreEqual(120, titles.Distinct().Count(), "Appended cards contain duplicates.");
                await Evidence.SaveAsync(page, "library-infinite-filters");

                // Request-only views are separate, optional selections.
                await page.GotoAsync("/Library?availability=MyRequests");
                await Assertions.Expect(cards).ToHaveCountAsync(1);
                await Assertions.Expect(cards).ToContainTextAsync("Film 121");
                await page.GotoAsync("/Library?availability=OtherRequests");
                await Assertions.Expect(cards).ToHaveCountAsync(1);
                await Assertions.Expect(cards).ToContainTextAsync("Film 122");
                await page.SetViewportSizeAsync(390, 844);
                await page.GetByRole(AriaRole.Button, new() { Name = "Open filter", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("#library-filter [name=availability][value=OtherRequests]")).ToBeCheckedAsync();
                await Assertions.Expect(page.Locator("#library-filter [name=availability][value=Available]")).Not.ToBeCheckedAsync();
                await page.Locator("#library-filter").EvaluateAsync("async element => { await Promise.all(element.getAnimations({subtree: true}).map(animation => animation.finished.catch(() => {}))); }");
                await Evidence.SaveAsync(page, "library-filters-mobile");

                await using var native = await session.Context.Browser!.NewContextAsync(new() { BaseURL = jularr.Address.ToString(), JavaScriptEnabled = false, StorageState = await session.Context.StorageStateAsync() });
                var plain = await native.NewPageAsync();
                await plain.GotoAsync("/Library?sort=Title&genre=Drama");
                await plain.GetByRole(AriaRole.Link, new() { Name = "Load more", Exact = true }).ClickAsync();
                await Assertions.Expect(plain.Locator(".library-grid .media-card")).ToHaveCountAsync(48);
                StringAssert.Contains(plain.Url, "p=2");
                StringAssert.Contains(plain.Url, "genre=Drama");
            }
            catch
            {
                await Evidence.SaveAsync(page, "library-infinite-filters-failed");
                throw;
            }
        }
    }
}
