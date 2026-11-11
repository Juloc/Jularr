using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Every cover has the same size, also the ones of the continue row; on a phone the rows and grids show more, smaller covers.
    [TestClass]
    public sealed class CoverSizeTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Covers_HaveOneSizeEverywhere_AndAPhoneShowsThreeInARow()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);

            // Six films with a file; the first three are begun by the profile (the continue row), all of them are in the library grid.
            await jularr.RunSqlAsync("""
                DO $$
                DECLARE pid bigint; mount bigint; root bigint; ed bigint; ver bigint; asset bigint; n integer;
                BEGIN
                    SELECT "Id" INTO pid FROM "Profiles" ORDER BY "Id" LIMIT 1;
                    INSERT INTO "StorageMounts" ("Name", "StorageProtocolTypeId", "SharePath", "IsReadOnly") VALUES ('Sizes', 3, 'C:\sizes', FALSE) RETURNING "Id" INTO mount;
                    INSERT INTO "LibraryRoots" ("Name", "MediaTypeId", "StorageMountId", "RelativePath") VALUES ('Size films', 1, mount, 'films') RETURNING "Id" INTO root;
                    FOR n IN 1..6 LOOP
                        INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (7300 + n, 1, 'Size Film ' || n, 2020 + n);
                        INSERT INTO "WorkEditions" ("WorkId", "LanguageCode") VALUES (7300 + n, 'en') RETURNING "Id" INTO ed;
                        INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId") VALUES (7300 + n, ed) RETURNING "Id" INTO ver;
                        INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "MediaAssetTypeId") VALUES (7300 + n, ver, 1) RETURNING "Id" INTO asset;
                        INSERT INTO "StoredFiles" ("LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "MediaAssetId", "LastSeenAt", "ProbedAt", "Container", "DurationMs", "QualityKey")
                        VALUES (root, 'films/' || n || '.mkv', 1000, now(), asset, now(), now(), 'matroska', 6000000, 'WEBDL-1080p');
                        IF n <= 3 THEN
                            INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "ProgressPositionTypeId", "PositionMs", "DurationMs", "UpdatedAt")
                            VALUES (pid, 7300 + n, 1, 600000, 6000000, now() - n * interval '1 minute');
                        END IF;
                    END LOOP;
                END $$;
                """);

            foreach (var (width, height, name) in new[] { (1440, 900, "desktop"), (768, 1024, "tablet"), (375, 812, "phone") })
            {
                await page.SetViewportSizeAsync(width, height);

                // The home page: the continue row and the next row hold covers of the same width and height.
                await page.GotoAsync("/");
                var resume = page.Locator(".media-card--resume");
                await Assertions.Expect(resume).ToHaveCountAsync(3);
                var plain = page.Locator(".media-card:not(.media-card--resume) .media-card__surface");
                await Assertions.Expect(plain.First).ToBeVisibleAsync();
                var resumeBox = (await resume.First.Locator(".media-card__surface").BoundingBoxAsync())!;
                var plainBox = (await plain.First.BoundingBoxAsync())!;
                Assert.AreEqual(Math.Round(plainBox.Width), Math.Round(resumeBox.Width), $"{name}: the continue cover is as wide as the others");
                Assert.AreEqual(Math.Round(plainBox.Height), Math.Round(resumeBox.Height), $"{name}: the continue cover is as high as the others");
                await Evidence.SaveAsync(page, $"cover-size-home-{name}");

                // The library grid: nothing sticks out of the page, and a phone shows three covers in a row.
                await page.GotoAsync("/Library");
                var cards = page.Locator(".media-grid .media-card");
                await Assertions.Expect(cards).ToHaveCountAsync(6);
                var tops = new List<double>();
                foreach (var card in await cards.AllAsync()) tops.Add(Math.Round((await card.BoundingBoxAsync())!.Y));
                var firstRow = tops.Count(top => top == tops[0]);
                if (name == "phone") Assert.IsTrue(firstRow >= 3, $"the phone shows {firstRow} covers in a row, expected at least three");
                Assert.IsFalse(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth"), $"{name}: the page scrolls sideways");
                await Evidence.SaveAsync(page, $"cover-size-library-{name}");
            }
        }
    }
}
