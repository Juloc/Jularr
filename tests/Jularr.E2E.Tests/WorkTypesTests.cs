using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The management page for anime (a second number and a switch between the numberings) and for manga (volumes in one table, with their files).
    [TestClass]
    public sealed class WorkTypesTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Anime_ShowsAbsoluteNumbers_AndSwitchesToAniListNumbers_AndManga_ShowsItsVolumes()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);

                // An anime of two seasons; the second is split into two AniList entries after its second episode.
                var anime = await CreateAsync(page, "Series", "Anime", "Test Anime");
                await jularr.RunSqlAsync($"""
                    INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number") VALUES ({anime}, 1, 1), ({anime}, 1, 2);
                    INSERT INTO "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number", "ReleasedOn")
                    SELECT {anime}, season."Id", 2, episode."Number", date '2020-01-01' + episode."Number"
                    FROM "WorkUnits" AS season CROSS JOIN generate_series(1, 3) AS episode ("Number")
                    WHERE season."WorkId" = {anime} AND season."WorkUnitTypeId" = 1;
                    INSERT INTO "WorkUnitExternalIdentities" ("WorkUnitId", "Namespace", "Value", "EpisodeOffset")
                    SELECT season."Id", 'anilist', CASE WHEN offsets."Offset" = 0 THEN '1001' ELSE '1002' END, offsets."Offset"
                    FROM "WorkUnits" AS season CROSS JOIN (VALUES (0), (2)) AS offsets ("Offset")
                    WHERE season."WorkId" = {anime} AND season."WorkUnitTypeId" = 1 AND season."Number" = 2;
                    """);
                await page.GotoAsync("/Admin/Works/" + anime);
                await Assertions.Expect(page.Locator("th[data-col=absolute]").First).ToBeVisibleAsync();
                var season = page.Locator(".work-season").First;
                var third = season.Locator("tr.work-row").Filter(new() { Has = page.Locator("td[data-col=number]").GetByText("3", new() { Exact = true }) });
                await Assertions.Expect(third.Locator("td[data-col=absolute]")).ToHaveTextAsync("6");
                await Assertions.Expect(third.Locator("td[data-col=number] [data-numbering=standard]")).ToBeVisibleAsync();

                // The AniList numbering only changes what is shown: the third episode of the season is the first of its second entry.
                await page.GetByRole(AriaRole.Button, new() { Name = "AniList", Exact = true }).ClickAsync();
                await Assertions.Expect(third.Locator("td[data-col=number] [data-numbering=anilist]")).ToHaveTextAsync("1");
                await Assertions.Expect(third.Locator("td[data-col=number] [data-numbering=standard]")).ToBeHiddenAsync();
                await page.ReloadAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "AniList", Exact = true })).ToHaveAttributeAsync("aria-pressed", "true");
                await Assertions.Expect(third.Locator("td[data-col=number] [data-numbering=anilist]")).ToBeVisibleAsync();

                // A manga has its volumes in one table without seasons.
                var manga = await CreateAsync(page, "Book", "Manga", "Test Manga");
                await jularr.RunSqlAsync($"""
                    INSERT INTO "WorkUnits" ("WorkId", "WorkUnitTypeId", "Number", "ReleasedOn") VALUES ({manga}, 3, 1, date '2020-01-01'), ({manga}, 3, 2, date '2020-06-01');
                    """);
                await page.GotoAsync("/Admin/Works/" + manga);
                foreach (var tab in new[] { "Volumes", "History", "Manage files", "Metadata" })
                {
                    await Assertions.Expect(page.Locator(".navigation-tabs__link", new() { HasText = tab })).ToBeVisibleAsync();
                }

                Assert.AreEqual(0, await page.Locator(".work-season").CountAsync(), "A manga has no seasons.");
                var rows = page.Locator("[data-work-table=volumes] tr.work-row");
                Assert.AreEqual(2, await rows.CountAsync());
                StringAssert.Contains(await rows.First.InnerTextAsync(), "Volume 2");
                StringAssert.Contains(await rows.First.InnerTextAsync(), "Missing");
                await Assertions.Expect(page.Locator("th[data-col=released]").First).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator("th[data-col=pages]").First).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "work-manga-volumes");

                // An album lists its tracks in order, with no switch and no search of their own: the album is monitored and searched as a whole.
                var album = await CreateAsync(page, "Music", "None", "Test Album");
                await jularr.RunSqlAsync($"INSERT INTO \"WorkUnits\" (\"WorkId\", \"WorkUnitTypeId\", \"Number\", \"DiscNumber\") VALUES ({album}, 5, 1, 1), ({album}, 5, 2, 1), ({album}, 5, 3, 2);");
                await page.GotoAsync("/Admin/Works/" + album);
                await Assertions.Expect(page.Locator(".navigation-tabs__link", new() { HasText = "Tracks" })).ToBeVisibleAsync();
                var tracks = page.Locator("[data-work-table=tracks] tr.work-row");
                Assert.AreEqual(3, await tracks.CountAsync());
                await Assertions.Expect(tracks.First).ToContainTextAsync("Track 1");
                Assert.AreEqual(2, await page.Locator(".work-season").CountAsync(), "Two discs, two accordions.");
                await Assertions.Expect(page.Locator(".work-season").Last).ToContainTextAsync("Disc 2");
                Assert.AreEqual(0, await page.Locator("[data-work-table=tracks] th[data-col=monitored]").CountAsync());
                await Assertions.Expect(page.Locator("th[data-col=length]").First).ToBeVisibleAsync();

                // A book shows its editions with their facts, a game its platforms, each wanted on its own.
                var novel = await CreateAsync(page, "Book", "None", "Test Novel");
                await jularr.RunSqlAsync($"""
                    INSERT INTO "WorkEditions" ("Id", "WorkId", "LanguageCode", "EditionFormatId", "Publisher", "ReleaseYear") VALUES (810, {novel}, 'en', 2, 'Test House', 2019), (811, {novel}, 'de', 1, NULL, NULL);
                    INSERT INTO "WorkEditionExternalIdentities" ("WorkEditionId", "Namespace", "Value") VALUES (810, 'isbn', '9780000000001');
                    """);
                await page.GotoAsync("/Admin/Works/" + novel);
                await Assertions.Expect(page.Locator(".navigation-tabs__link", new() { HasText = "Editions" })).ToBeVisibleAsync();
                Assert.AreEqual(2, await page.Locator(".work-season").CountAsync());
                await Assertions.Expect(page.Locator(".work-season").First).ToContainTextAsync("ISBN 9780000000001");
                await Assertions.Expect(page.Locator(".work-season").First).ToContainTextAsync("E-book");
                Assert.AreEqual(0, await page.Locator(".navigation-tabs__link", new() { HasText = "Manage files" }).CountAsync(), "A book has no Manage files tab.");

                var game = await CreateAsync(page, "Game", "None", "Test Game");
                await jularr.RunSqlAsync($"""
                    INSERT INTO "GamePlatforms" ("Id", "Name", "Abbreviation") VALUES (710, 'Switch', 'NSW'), (711, 'PC', NULL);
                    INSERT INTO "WorkPlatforms" ("WorkId", "GamePlatformId") VALUES ({game}, 710), ({game}, 711);
                    INSERT INTO "WantedItems" ("WorkId", "GamePlatformId") VALUES ({game}, 710);
                    """);
                await page.GotoAsync("/Admin/Works/" + game);
                await Assertions.Expect(page.Locator(".navigation-tabs__link", new() { HasText = "Platforms" })).ToBeVisibleAsync();
                var platforms = page.Locator(".work-season");
                Assert.AreEqual(2, await platforms.CountAsync());
                await Assertions.Expect(platforms.Filter(new() { HasText = "PC" })).ToContainTextAsync("Not requested");

                // Each platform has its own target with its own settings.
                await platforms.Filter(new() { HasText = "Switch" }).GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("dialog[open].work-dialog")).ToBeVisibleAsync();
                await page.Keyboard.PressAsync("Escape");
                await Evidence.SaveAsync(page, "work-game-platforms");
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-work-types");
                throw;
            }
        }

        // The machine translations of a book's file are listed on its page with the language, the coverage and a delete.
        [TestMethod]
        [Timeout(300_000)]
        public async Task Book_ListsItsTranslations_AndClearsOneLanguage()
        {
            using var folders = new Folders("Books");
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Books", "Book"));
                var book = await CreateAsync(page, "Book", "None", "Translated Novel");
                await jularr.RunSqlAsync($"""
                    UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" IN ('Reading', 'Translation');
                    INSERT INTO "WorkEditions" ("Id", "WorkId", "LanguageCode", "EditionFormatId") VALUES (820, {book}, 'en', 2), (821, {book}, 'de', 2);
                    INSERT INTO "WorkVersions" ("Id", "WorkId", "WorkEditionId") VALUES (820, {book}, 820);
                    INSERT INTO "MediaAssets" ("Id", "WorkId", "WorkVersionId", "MediaAssetTypeId") VALUES (820, {book}, 820, 1);
                    INSERT INTO "StoredFiles" ("Id", "LibraryRootId", "RelativePath", "SizeBytes", "ModifiedAt", "MediaAssetId", "LastSeenAt")
                    SELECT 820, (SELECT min("Id") FROM "LibraryRoots" WHERE "Name" = 'Books'), 'Translated Novel/Translated Novel.epub', 100, now(), 820, now();
                    INSERT INTO "ReadingDocuments" ("Id", "StoredFileId", "SourceSizeBytes", "SourceModifiedAt", "ContentHash", "AnalyzerVersion", "ReadingFormatTypeId", "Structure", "TotalCharacters")
                    VALUES (820, 820, 100, now(), decode(repeat('cd', 32), 'hex'), 1, 1, '[]', 0);
                    INSERT INTO "ReadingResources" ("ReadingDocumentId", "Path", "MediaType", "Bytes")
                    SELECT 820, item."Path", 'text/html', convert_to('0123456789', 'UTF8')
                    FROM (VALUES (':section/00000'), (':section/00001'), (':translation/de/00000')) AS item ("Path");
                    """);
                await page.GotoAsync("/Admin/Works/" + book);
                var translations = page.Locator(".work-season", new() { HasText = "Translations" });
                await Assertions.Expect(translations).ToBeVisibleAsync();
                await translations.GetByRole(AriaRole.Button, new() { Name = "Show or hide this section" }).ClickAsync();
                await Assertions.Expect(translations.Locator("tr.work-row")).ToContainTextAsync("50 %");

                // The edition in German has no file but reads translated.
                await Assertions.Expect(page.Locator(".work-season", new() { HasText = "German" }).First).ToContainTextAsync("Reads translated");
                await Steps.SubmitAsync(page, translations.Locator("tr.work-row form"));
                await Assertions.Expect(page.Locator(".work-season", new() { HasText = "Translations" })).ToHaveCountAsync(0);
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "failure-work-translations");
                throw;
            }
        }

        // A title made by hand on the Works page, as far as its management page.
        private static async Task<string> CreateAsync(IPage page, string type, string subtype, string title)
        {
            await page.GotoAsync("/Admin/Works");
            var form = page.Locator("main form", new() { Has = page.Locator("[name=CanonicalTitle]") });
            await form.Locator("[name=MediaTypeId]").SelectOptionAsync(new SelectOptionValue { Label = type });
            await form.Locator("[name=MediaSubtypeId]").SelectOptionAsync(new SelectOptionValue { Label = subtype });
            await form.Locator("[name=CanonicalTitle]").FillAsync(title);
            await Steps.SubmitAsync(page, form);
            return Regex.Match(page.Url, @"/Admin/Works/(\d+)").Groups[1].Value;
        }
    }
}
