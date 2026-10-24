using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class DetailsTabsTests
    {
        [TestMethod]
        [Timeout(240_000)]
        public async Task Details_TabsPreviewsHeroAndEmptyTabs_WorkWithAndWithoutScript()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);

            // A series of two seasons with people and some progress, and a film with nothing but its title.
            await jularr.RunSqlAsync("""
                DO $$
                DECLARE pid bigint; mount bigint; root bigint; ed bigint; ver bigint; season bigint; unit bigint; asset bigint; person bigint; n integer; s integer;
                BEGIN
                    SELECT "Id" INTO pid FROM "Profiles" ORDER BY "Id" LIMIT 1;
                    INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") VALUES ('Tabs', 3, 'C:\tabs', FALSE) RETURNING "Id" INTO mount;
                    INSERT INTO "LibraryRoots" ("Name", "MediaTypeId", "StorageMountId", "RelativePath") VALUES ('Tabs series', 2, mount, 'series') RETURNING "Id" INTO root;
                    INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (7001, 2, 'Tabbed Show', 2024), (7002, 1, 'Bare Film', 2020);
                    INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") VALUES (7001, 'Overview', 'A show with two seasons.', TRUE), (7001, 'Genres', 'Drama', TRUE);
                    INSERT INTO "ExternalWorks" ("Namespace", "Value", "MediaTypeId", "Title", "ReleaseYear", "Rating", "Votes") VALUES ('tmdb_tv', '777', 2, 'Tabbed Show', 2024, 8.4, 100);
                    INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES (7001, 'tmdb_tv', '777');
                    INSERT INTO "WorkEditions" ("WorkId", "LanguageCode") VALUES (7001, 'en') RETURNING "Id" INTO ed;
                    INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId") VALUES (7001, ed) RETURNING "Id" INTO ver;
                    FOR s IN 1..2 LOOP
                        INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number") VALUES (7001, 1, s) RETURNING "Id" INTO season;
                        FOR n IN 1..8 LOOP
                            INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "Title", "ReleasedOn", "RuntimeMinutes")
                            VALUES (7001, season, 2, n, 'Episode ' || n, current_date - 100 + n, 45) RETURNING "Id" INTO unit;
                            IF s = 1 AND n <= 6 THEN
                                INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "WorkUnitId", "MediaAssetTypeId") VALUES (7001, ver, unit, 1) RETURNING "Id" INTO asset;
                                INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "MediaAssetId", "LastSeenAt", "ProbedAt", "Container", "DurationMs", "QualityKey")
                                VALUES (root, 'tabs/' || n || '.mkv', 1000, now(), asset, now(), now(), 'matroska', 2700000, 'WEBDL-1080p');
                            END IF;
                            IF s = 1 AND n <= 2 THEN
                                INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs", "DurationMs", "IsCompleted", "CompletedAt")
                                VALUES (pid, 7001, unit, 1, 2700000, 2700000, TRUE, now());
                            END IF;
                        END LOOP;
                    END LOOP;
                    FOR n IN 1..10 LOOP
                        INSERT INTO "People" ("Name") VALUES ('Person ' || n) RETURNING "Id" INTO person;
                        INSERT INTO "WorkPeople" ("WorkId", "PersonId", "PersonRoleId", "Position", "Credit") VALUES (7001, person, 1, n, 'Role ' || n);
                    END LOOP;
                END $$;
                """);

            // The Overview: a plain cover, a standalone availability chip, and actions aligned with progress.
            await page.GotoAsync("/Details/7001");
            var hero = page.Locator(".media-hero--poster");
            await Assertions.Expect(hero.Locator(".media-hero__poster .media-availability-badge")).ToHaveCountAsync(0);
            await Assertions.Expect(hero.Locator(".media-hero__chips .media-availability-badge--inline")).ToBeVisibleAsync();

            await page.SetViewportSizeAsync(1440, 900);
            var posterBox = await hero.Locator(".media-hero__poster").BoundingBoxAsync();
            var contentBox = await hero.Locator(".media-hero__content").BoundingBoxAsync();
            var footerBox = await hero.Locator(".media-hero__footer").BoundingBoxAsync();
            var actionsBox = await hero.Locator(".media-hero__actions").BoundingBoxAsync();
            var progressBox = await hero.Locator(".media-hero__progress").BoundingBoxAsync();
            Assert.IsNotNull(posterBox);
            Assert.IsNotNull(contentBox);
            Assert.IsNotNull(footerBox);
            Assert.IsNotNull(actionsBox);
            Assert.IsNotNull(progressBox);
            Assert.IsTrue(Math.Abs(contentBox.Y - posterBox.Y) <= 2, "The poster and description must start at the same height.");
            Assert.IsTrue(Math.Abs(posterBox.Height / posterBox.Width - 1.5) <= .04, "Desktop artwork must keep its natural 2:3 cover ratio.");
            Assert.IsTrue(footerBox.Y >= Math.Max(contentBox.Y + contentBox.Height, posterBox.Y + posterBox.Height), "Actions must be below the cover and description.");
            Assert.IsTrue(Math.Abs(actionsBox.Y + actionsBox.Height - (progressBox.Y + progressBox.Height)) <= 18, "Actions and progress must share a bottom row.");

            await page.SetViewportSizeAsync(390, 844);
            await Assertions.Expect(hero.Locator(".media-hero__poster")).ToBeHiddenAsync();
            await Assertions.Expect(hero.Locator(".media-hero__backdrop")).ToBeVisibleAsync();
            await Assertions.Expect(hero.Locator(".media-hero__chips .media-availability-badge--inline")).ToBeVisibleAsync();
            var mobileActions = await hero.Locator(".media-hero__actions").BoundingBoxAsync();
            var mobileProgress = await hero.Locator(".media-hero__progress").BoundingBoxAsync();
            Assert.IsNotNull(mobileActions);
            Assert.IsNotNull(mobileProgress);
            Assert.IsTrue(mobileProgress.Y >= mobileActions.Y + mobileActions.Height, "Mobile progress must follow the actions.");
            await page.SetViewportSizeAsync(1440, 900);
            await Assertions.Expect(hero.Locator(".media-hero__progress-bar")).ToHaveAttributeAsync("value", new Regex("^(1[0-9]|2[0-9])$"));
            await Assertions.Expect(hero.Locator(".media-hero__title")).ToHaveTextAsync("Tabbed Show");
            await Assertions.Expect(page.Locator(".navigation-tabs")).ToHaveCountAsync(1);
            var tabs = page.Locator(".details-tabs .navigation-tabs__link");
            await Assertions.Expect(tabs).ToHaveTextAsync(new[] { new Regex("Overview"), new Regex("Episodes"), new Regex("People"), new Regex("Details"), new Regex("Files") });
            await Assertions.Expect(page.Locator(".details-panel .episode-card")).ToHaveCountAsync(5);
            await Assertions.Expect(page.Locator(".details-person")).ToHaveCountAsync(8);
            await Assertions.Expect(page.Locator(".details-block__all")).ToHaveCountAsync(4);
            await Evidence.SaveAsync(page, "details-overview");

            // A tab is its own address: only its block shows, in full.
            await page.Locator(".details-block__all").First.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"tab=episodes"));
            await Assertions.Expect(page.Locator(".details-block__heading")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".details-panel .episode-card")).ToHaveCountAsync(8);
            await Assertions.Expect(page.Locator(".details-toolbar")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".details-person")).ToHaveCountAsync(0);
            await page.Locator(".details-tabs .navigation-tabs__link", new() { HasText = "People" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"tab=people"));
            await Assertions.Expect(page.Locator(".details-person")).ToHaveCountAsync(10);

            // Without script the same links and addresses work.
            var state = await session.Context.StorageStateAsync();
            await using var plain = await session.Context.Browser!.NewContextAsync(new() { BaseURL = jularr.Address.ToString(), JavaScriptEnabled = false, StorageState = state });
            var bare = await plain.NewPageAsync();
            await bare.GotoAsync("/Details/7001?tab=episodes&view=cards");
            await Assertions.Expect(bare.Locator(".details-card--grid .episode-card")).ToHaveCountAsync(8);
            await bare.Locator(".details-tabs .navigation-tabs__link", new() { HasText = "Details" }).ClickAsync();
            await Assertions.Expect(bare).ToHaveURLAsync(new Regex(@"tab=details"));
            await Assertions.Expect(bare.Locator(".fact-grid")).ToBeVisibleAsync();

            // Tabs without content are not there at all.
            await page.GotoAsync("/Details/7002");
            await Assertions.Expect(page.Locator(".details-tabs .navigation-tabs__link")).ToHaveTextAsync(new[] { new Regex("Overview"), new Regex("Details") });
        }
    }
}
