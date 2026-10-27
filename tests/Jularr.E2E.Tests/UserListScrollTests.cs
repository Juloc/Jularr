using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class UserListScrollTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task CatalogsRequestsAndNotifications_AppendBoundedPagesAndKeepActions()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                DO $$ DECLARE pid bigint; aid bigint;
                BEGIN
                    SELECT "Id" INTO pid FROM "Profiles" ORDER BY "Id" LIMIT 1;
                    SELECT "Id" INTO aid FROM "Accounts" ORDER BY "Id" LIMIT 1;
                    UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" IN ('Library', 'Collections', 'Franchises', 'Requests', 'Notifications');
                    INSERT INTO "Franchises" ("Id", "Name") VALUES (9001, 'Scroll series');
                    INSERT INTO "People" ("Id", "Name") VALUES (9001, 'Scroll actor');
                    INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "FranchiseId")
                    SELECT n, 1, 'Film ' || lpad(n::text, 3, '0'), 9001 FROM generate_series(1, 120) AS n;
                    INSERT INTO "WorkPeople" ("WorkId", "PersonId", "PersonRoleId", "Position") SELECT "Id", 9001, 1, 1 FROM "Works";
                    INSERT INTO "Collections" ("Id", "ProfileId", "Name") VALUES (9001, pid, 'Scroll collection');
                    INSERT INTO "CollectionWorks" ("CollectionId", "WorkId", "Position") SELECT 9001, "Id", "Id"::integer FROM "Works";
                    INSERT INTO "WatchlistEntries" ("ProfileId", "WorkId") SELECT pid, "Id" FROM "Works";
                    INSERT INTO "AcquisitionRequests" ("WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId") SELECT "Id", pid, aid, 1 FROM "Works";
                    INSERT INTO "Notifications" ("ProfileId", "NotificationTypeId", "WorkId", "Subject", "Link")
                    SELECT pid, 1, "Id", "CanonicalTitle", '/Details/' || "Id" FROM "Works";
                END $$;
                ANALYZE;
                """);
            try
            {
                foreach (var (address, selector, count) in new[] {
                    ("/People/9001", ".person-grid > .media-card", 120),
                    ("/Franchise/9001", ".franchise-grid > .media-card", 120),
                    ("/Collections/9001", ".collection-grid > .media-grid__entry", 119),
                    ("/Requests?state=All&type=movies", ".requests-list > .request-row", 120),
                    ("/Notifications", ".notifications > .notifications__item", 120),
                    ("/Favorites?sort=Title&type=movies", ".favorites-grid > .favorites-entry", 119) })
                {
                    await page.GotoAsync(address);
                    var entries = page.Locator(selector);
                    await Assertions.Expect(entries).ToHaveCountAsync(48);
                    await page.Locator("[data-infinite-list]").ScrollIntoViewIfNeededAsync();
                    await Assertions.Expect(entries).ToHaveCountAsync(96);
                    await page.Locator("[data-infinite-list]").ScrollIntoViewIfNeededAsync();
                    await Assertions.Expect(entries).ToHaveCountAsync(count);
                    await Assertions.Expect(page.Locator("[data-infinite-next]")).ToHaveCountAsync(0);
                    var keys = await entries.EvaluateAllAsync<string[]>("entries => entries.map(entry => entry.dataset.listKey)");
                    Assert.AreEqual(count, keys.Distinct().Count(), address + " contains duplicates.");

                    if (address.StartsWith("/People", StringComparison.Ordinal))
                    {
                        // A form inside an appended card must retain all loaded pages after returning.
                        await entries.Nth(70).GetByRole(AriaRole.Button, new() { Name = "Remove from favorites", Exact = true }).ClickAsync();
                        await Assertions.Expect(entries).ToHaveCountAsync(120);
                        await Assertions.Expect(entries.Nth(70).GetByRole(AriaRole.Button, new() { Name = "Add to favorites", Exact = true })).ToBeVisibleAsync();
                        Assert.AreEqual(new Uri(jularr.Address, address).ToString(), page.Url);
                    }

                    if (address.StartsWith("/Requests", StringComparison.Ordinal))
                    {
                        await entries.Nth(70).Locator(".request-row__actions a[href*='request=']").ClickAsync();
                        await Assertions.Expect(page.Locator(".request-drawer__head")).ToBeVisibleAsync();
                        await page.GoBackAsync();
                        await Assertions.Expect(entries).ToHaveCountAsync(120);
                    }
                }

                // Appended favorites retain their collection memberships and delegated controls.
                var favorite = page.Locator(".favorites-entry").Nth(70);
                var title = await favorite.GetAttributeAsync("data-title");
                await favorite.Locator("[data-favorites-picker]").First.ClickAsync();
                await Assertions.Expect(page.Locator("[data-favorites-subject]")).ToHaveTextAsync(title!);
                await Assertions.Expect(page.Locator("#favorites-picker [name=collectionIds][value='9001']")).ToBeCheckedAsync();
                await Evidence.SaveAsync(page, "user-lists-appended-favorite");

                await using var native = await session.Context.Browser!.NewContextAsync(new() { BaseURL = jularr.Address.ToString(), JavaScriptEnabled = false, StorageState = await session.Context.StorageStateAsync() });
                var plain = await native.NewPageAsync();
                await plain.GotoAsync("/People/9001");
                await plain.GetByRole(AriaRole.Link, new() { Name = "Load more", Exact = true }).ClickAsync();
                await Assertions.Expect(plain.Locator(".person-grid > .media-card")).ToHaveCountAsync(48);
                StringAssert.Contains(plain.Url, "pageNumber=2");
            }
            catch
            {
                await Evidence.SaveAsync(page, "user-lists-scroll-failed");
                throw;
            }
        }
    }
}
