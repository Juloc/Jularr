using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class ContentAvailabilityTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Settings_LibraryAndTrending_UseBothContentLanguagesAndMatchingSubtitles()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "CatalogNamespace", "ReleasedOn", "ImageUrl", "TrendingRank", "Popularity", "Rating", "Votes") VALUES
                    (7801, 1, 'Accepted film', 'tmdb_movie', '2000-01-01', 'https://127.0.0.1/7801.jpg', 1, 80, 8, 100),
                    (7802, 1, 'Subtitled film', 'tmdb_movie', '2000-01-01', 'https://127.0.0.1/7802.jpg', 2, 60, 8, 100),
                    (7803, 3, 'Accepted book', NULL, NULL, NULL, NULL, NULL, NULL, NULL);
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" = 'LocalDiscovery';
                UPDATE "ExternalWorkSyncs" SET "CompletedAt" = now() WHERE "Namespace" = 'tmdb_movie';
                -- Only the films are a whole catalog here; the other catalogs keep Discover's lists and walk no provider.
                UPDATE "ExternalWorkSyncs" SET "CacheTier" = 2 WHERE "Namespace" <> 'tmdb_movie';
                INSERT INTO "WorkExternalIdentities" ("WorkId", "Namespace", "Value") VALUES
                    (7801, 'tmdb_movie', '7801'), (7802, 'tmdb_movie', '7802');
                """);
            await jularr.SeedLibraryFilesAsync();
            await jularr.RunSqlAsync("""
                UPDATE "WorkEditions" SET "LanguageCode" = 'en' WHERE "WorkId" = 7803;
                INSERT INTO "WantedItems" ("WorkId", "IsPaused") VALUES (7801, TRUE), (7802, FALSE), (7803, FALSE);
                INSERT INTO "MediaTracks" ("StoredFileId", "StreamIndex", "MediaTrackTypeId", "Language", "IsDefault", "IsForced")
                SELECT file."Id", 0, 2, CASE WHEN asset."WorkId" = 7801 THEN 'eng' ELSE 'jpn' END, TRUE, FALSE
                FROM "StoredFiles" AS file JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId" WHERE asset."WorkId" IN (7801, 7802);
                INSERT INTO "MediaTracks" ("StoredFileId", "StreamIndex", "MediaTrackTypeId", "Language", "IsDefault", "IsForced")
                SELECT "Id", 1, 3, 'deu', FALSE, FALSE FROM "StoredFiles" WHERE "MediaAssetId" = 7802;
                """);

            // Content choices save with the profile; the menu language remains English.
            await page.GotoAsync("/Settings");
            await page.Locator("select[name=FirstContentLanguage]").SelectOptionAsync("de");
            await page.Locator("select[name=SecondContentLanguage]").SelectOptionAsync("en");
            await Evidence.SaveAsync(page, "content-languages-settings");
            await Steps.FollowAsync(page, page.Locator("[data-save-pill]").GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }));
            await page.ReloadAsync();
            await Assertions.Expect(page.Locator("select[name=FirstContentLanguage]")).ToHaveValueAsync("de");
            await Assertions.Expect(page.Locator("select[name=SecondContentLanguage]")).ToHaveValueAsync("en");

            foreach (var width in new[] { 1280, 390 })
            {
                await page.SetViewportSizeAsync(width, 844);
                await page.GotoAsync("/Library");
                var film = page.Locator(".media-card").Filter(new() { HasText = "Accepted film" });
                var book = page.Locator(".media-card").Filter(new() { HasText = "Accepted book" });
                var subtitled = page.Locator(".media-card").Filter(new() { HasText = "Subtitled film" });
                await Assertions.Expect(film.Locator(".media-availability-badge--profileaudio")).ToBeVisibleAsync();
                await Assertions.Expect(book.Locator(".media-availability-badge--profileaudio")).ToBeVisibleAsync();
                await Assertions.Expect(subtitled.Locator(".media-availability-badge--preferredsubtitles")).ToHaveAttributeAsync("aria-label", "Available with subtitles in a preferred language.");
                await Assertions.Expect(page.Locator(".media-availability-badge--searching")).ToHaveCountAsync(0);
                await Evidence.SaveAsync(page, "content-availability-library-" + width);

                // Cached Trending gets current local availability, including paused-but-present media.
                await page.GotoAsync("/?type=movies&view=Trending");
                await Assertions.Expect(page.Locator(".media-card").Filter(new() { HasText = "Accepted film" }).Locator(".media-availability-badge--profileaudio")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator(".media-card").Filter(new() { HasText = "Subtitled film" }).Locator(".media-availability-badge--preferredsubtitles")).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "content-availability-trending-" + width);
            }
        }
    }
}
