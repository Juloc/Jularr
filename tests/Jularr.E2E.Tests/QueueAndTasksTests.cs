using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // As Sonarr's: the downloads are the Queue of the ARR group (Queue, History, Blocked), Jularr's own work the Tasks of the Jobs entry beside its Schedule;
    // each entry wears its own figure, and the old Activity addresses lead to the Queue.
    [TestClass]
    public sealed class QueueAndTasksTests
    {
        [TestMethod]
        [Timeout(300_000)]
        public async Task Queue_HoldsTheDownloads_AndTasks_TheRestWithRetryAndDismiss()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);

            // The offline keyless catalogs are switched off, so the background health check adds no task of its own.
            await jularr.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" IN ('Acquisition', 'Requests');
                UPDATE "Providers" SET "IsEnabled" = FALSE WHERE "ProtectedSecret" IS NULL;
                """);
            // Empty lists keep only Manual import in their bar, as in Sonarr; nothing else is offered without a row.
            foreach (var empty in new[] { "/Admin/Wanted", "/Admin/Queue" })
            {
                await page.GotoAsync(empty);
                await Assertions.Expect(page.Locator("main .navigation-tabs").First).ToBeVisibleAsync();
                var dock = page.Locator(".floating-pill.action-dock:not(.action-dock--form)");
                await Assertions.Expect(dock.Locator("a[href*='import=true']")).ToBeVisibleAsync();
                await Assertions.Expect(dock.Locator("form")).ToHaveCountAsync(0);
            }

            // A running download, a failed backup, a failed background job and a queued library search.
            await jularr.RunSqlAsync("""
                UPDATE "Operations" SET "DismissedAt" = now() WHERE "OperationStatusTypeId" = 4 AND "DismissedAt" IS NULL;
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (9601, 1, 'Queued Film', 2020), (9602, 1, 'Doubtful Film', 2021);
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (9601, 9601), (9602, 9602);
                INSERT INTO "AcquisitionReleases" ("Id", "WantedItemId", "ReleaseIdentity", "Title", "QualityKey", "AcquisitionReleaseStatusTypeId")
                VALUES (9601, 9601, 'queued-film', 'Queued.Film.2020.GERMAN.1080p.WEB-DL-JLR', 'WEBDL-1080p', 1), (9602, 9602, 'doubtful-film', 'Doubtful.Film.2021.1080p.WEB-DL-JLR', 'WEBDL-1080p', 1);
                INSERT INTO "Operations" ("OperationTypeId", "OperationStatusTypeId", "AcquisitionReleaseId", "DownloadStateTypeId", "ProgressPercent", "SizeBytes", "RemainingBytes", "EtaSeconds", "AttentionKey", "StartedAt")
                VALUES (2, 2, 9601, 2, 40, 7400000000, 4400000000, 720, NULL, now()), (2, 2, 9602, NULL, NULL, NULL, NULL, NULL, 'import_ambiguous_title', now());
                INSERT INTO "Operations" ("OperationTypeId", "OperationStatusTypeId", "ErrorKey", "FinishedAt") VALUES (3, 4, 'backup_version_mismatch', now());
                INSERT INTO "WorkerJobs" ("Key") VALUES ('operations') ON CONFLICT ("Key") DO NOTHING;
                INSERT INTO "Operations" ("OperationTypeId", "OperationStatusTypeId", "WorkerJobKey", "ErrorKey", "FinishedAt") VALUES (4, 4, 'operations', 'metadata_unavailable', now()) ON CONFLICT DO NOTHING;
                """);

            try
            {
                foreach (var (width, height, phone) in new[] { (1440, 900, false), (390, 844, true) })
                {
                    await page.SetViewportSizeAsync(width, height);

                    // An old address of the queue leads to the Queue: one table of the downloads only, as Sonarr's, the one that waits for a person first with its orange warning and reason.
                    await page.GotoAsync("/Admin/Activity?type=movies");
                    await Assertions.Expect(page).ToHaveURLAsync(new Regex("/Admin/Queue\\?type=movies$"));
                    await Assertions.Expect(page.Locator("main .list-panel")).ToHaveCountAsync(1);
                    await Assertions.Expect(page.Locator("main .list-body .list-row")).ToHaveCountAsync(2);
                    var running = Steps.ListRow(page, "Queued Film");
                    var doubt = Steps.ListRow(page, "Doubtful Film");
                    await Assertions.Expect(doubt).ToHaveClassAsync(new Regex("list-row--warning"));
                    await Assertions.Expect(doubt.Locator(".queue-reason--warning")).ToHaveTextAsync("The file's title does not clearly match the film");
                    await Assertions.Expect(page.Locator("main .list-body .list-row").First).ToContainTextAsync("Doubtful Film");
                    await Assertions.Expect(running).ToContainTextAsync("40%");
                    if (phone)
                    {
                        // The phone card: the state with its word, the bar and the time left under the title, the facts in one line.
                        await Assertions.Expect(running.Locator(".queue-state__label")).ToBeVisibleAsync();
                        await Assertions.Expect(running.Locator(".queue-progress-phone progress")).ToBeVisibleAsync();
                        await Assertions.Expect(running.Locator(".list-row__caption")).ToContainTextAsync("WEBDL-1080p");
                        await Assertions.Expect(running.Locator(".list-row__caption")).ToContainTextAsync("DE");
                    }
                    else
                    {
                        foreach (var column in new[] { "Episode", "Quality", "Time left", "Progress" }) await Assertions.Expect(page.Locator("main .list-head")).ToContainTextAsync(column);
                        await Assertions.Expect(running).ToContainTextAsync("WEBDL-1080p");
                        await Assertions.Expect(running.Locator(".queue-progress progress").First).ToHaveAttributeAsync("value", "40");
                        await Assertions.Expect(running.Locator(".queue-progress-phone")).ToBeHiddenAsync();
                    }

                    var views = page.Locator("main .navigation-tabs").First;
                    await Assertions.Expect(views.Locator("a[href^='/Admin/Queue/History']")).ToHaveCountAsync(1);
                    await Assertions.Expect(views.Locator("a[href^='/Admin/Queue/Blocked']")).ToHaveCountAsync(1);
                    await Evidence.SaveAsync(page, "queue-" + (phone ? "phone" : "desktop"));

                    // The Jobs entry opens its Tasks: the backup and the job need help (and the group counts what it holds), Schedule is the next chip.
                    await page.GotoAsync("/Admin/Jobs/Tasks");
                    var help = page.Locator("#tasks-group-Help");
                    await Assertions.Expect(Steps.ListRow(help, "Backup")).ToHaveCountAsync(1);
                    await Assertions.Expect(Steps.ListRow(help, "Library scans")).ToHaveCountAsync(1);
                    await Assertions.Expect(help).ToContainTextAsync("pg_dump or pg_restore is older than the PostgreSQL server.");
                    await Assertions.Expect(help.Locator(".activity-group__count")).ToHaveTextAsync((await help.Locator(".list-row").CountAsync()).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await Assertions.Expect(page.Locator("main")).Not.ToContainTextAsync("Queued Film");
                    await Assertions.Expect(page.Locator("main .navigation-tabs a[aria-current=page]")).ToHaveTextAsync("Tasks");
                    await Assertions.Expect(page.Locator("main .navigation-tabs a[href='/Admin/Jobs']")).ToHaveTextAsync("Schedule");
                    await Evidence.SaveAsync(page, "tasks-" + (phone ? "phone" : "desktop"));
                }

                // The sidebar: the Queue and Wanted in the ARR group, the Queue with its open download, Jobs with the tasks that need help.
                await page.SetViewportSizeAsync(1440, 900);
                var menu = page.Locator(".app-sidebar--admin .menu");
                var arr = menu.Locator(".menu-group", new() { Has = page.Locator("a.menu-entry[href='/Admin/Queue']") });
                await Assertions.Expect(arr.Locator("a.menu-entry[href='/Admin/Wanted']")).ToHaveCountAsync(1);
                await Assertions.Expect(arr.Locator("a.menu-entry[href='/Admin/Acquisition']")).ToHaveCountAsync(1);
                await Assertions.Expect(menu.Locator("a.menu-entry[href='/Admin/Queue'] [data-live-text=downloads]")).ToHaveTextAsync("2");
                var figure = menu.Locator("a.menu-entry[href='/Admin/Jobs/Tasks'] [data-live-text=jobs]");
                var needing = await page.Locator("#tasks-group-Help .list-row").CountAsync();
                await Assertions.Expect(figure).ToHaveTextAsync(needing.ToString(System.Globalization.CultureInfo.InvariantCulture));

                // Dismiss puts the backup aside: the job is left, and the figure follows.
                var backup = Steps.ListRow(page.Locator("#tasks-group-Help"), "Backup");
                await Steps.SubmitAsync(page, backup.Locator("form").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Dismiss" }) }));
                await Assertions.Expect(backup).ToHaveCountAsync(0);
                await Assertions.Expect(Steps.ListRow(page.Locator("#tasks-group-Help"), "Library scans")).ToHaveCountAsync(1);
                await page.ReloadAsync();
                await Assertions.Expect(figure).ToHaveTextAsync((needing - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch
            {
                await Evidence.SaveAsync(page, "queue-tasks-failed");
                throw;
            }
        }

        // A download whose folder holds only archives has nothing to assign: it does not wait in the Queue for ever, the download monitor fails it and the History says why in plain words.
        [TestMethod]
        [Timeout(300_000)]
        public async Task Queue_DownloadWithNothingToAssign_LeavesTheQueueAndTheHistoryTellsWhy()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);
            await jularr.RunSqlAsync("""
                UPDATE "InstanceModules" SET "IsEnabled" = TRUE WHERE "Key" IN ('Acquisition', 'Requests');
                UPDATE "Providers" SET "IsEnabled" = FALSE WHERE "ProtectedSecret" IS NULL;
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle", "ReleaseYear") VALUES (9611, 2, 'Packed Book', 2020), (9612, 2, 'Odd Book', 2021);
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (9611, 9611), (9612, 9612);
                INSERT INTO "AcquisitionReleases" ("Id", "WantedItemId", "ReleaseIdentity", "Title", "QualityKey", "AcquisitionReleaseStatusTypeId")
                VALUES (9611, 9611, 'packed-book', 'Packed.Book.2020.RETAiL.EPUB.eBook-GRP', 'EPUB', 1), (9612, 9612, 'odd-book', 'Odd.Book.2021.RETAiL.EPUB.eBook-GRP', 'EPUB', 1);
                INSERT INTO "Operations" ("OperationTypeId", "OperationStatusTypeId", "AcquisitionReleaseId", "AttentionKey", "AttentionProposal", "StartedAt")
                VALUES (2, 2, 9611, 'import_nothing_placeable', '{"Source":{"Directory":"x"},"Files":[{"Path":"vakr1xo.rar","Reason":"extension"}]}'::jsonb, now()),
                    (2, 2, 9612, 'import_nothing_placeable', '{"Source":{"Directory":"x"},"Files":[{"Path":"book.epub","Reason":null}]}'::jsonb, now());
                """);

            try
            {
                // Both wait for a person at first; the one with a file to assign keeps waiting, the one with only an archive fails with the next pass of the download monitor.
                await page.GotoAsync("/Admin/Queue");
                await Assertions.Expect(Steps.ListRow(page, "Packed Book")).ToHaveCountAsync(1);
                await Steps.WaitForDownloadMonitorAsync(page, async () =>
                {
                    await page.GotoAsync("/Admin/Queue");
                    return await Steps.ListRow(page, "Packed Book").CountAsync() == 0;
                }, "The download with only an archive did not leave the queue", TimeSpan.FromMinutes(3));
                await Assertions.Expect(Steps.ListRow(page, "Packed Book")).ToHaveCountAsync(0);
                await Assertions.Expect(Steps.ListRow(page, "Odd Book")).ToHaveCountAsync(1);
                await page.GotoAsync("/Admin/Queue/History");
                var failed = Steps.ListRow(page, "Packed Book");
                await Assertions.Expect(failed).ToHaveCountAsync(1);

                // The event's details say why, in plain words.
                await failed.Locator("[data-drawer-open]").ClickAsync();
                await Assertions.Expect(page.Locator("[id^='event-']", new() { HasText = "The download holds no file the import takes, only archives or release files" }).First).ToBeVisibleAsync();

                // Wanted follows the real state: the one that failed is no longer being downloaded, the one that still waits for a person is.
                await page.GotoAsync("/Admin/Wanted");
                await Assertions.Expect(Steps.ListRow(page, "Packed Book")).Not.ToContainTextAsync("Downloading");
                await Assertions.Expect(Steps.ListRow(page, "Odd Book")).ToContainTextAsync("Downloading");

                // The Queue, History and Wanted at desktop, tablet and phone width.
                foreach (var (width, height, name) in new[] { (1440, 900, "desktop"), (768, 1024, "tablet"), (375, 812, "phone") })
                {
                    await page.SetViewportSizeAsync(width, height);
                    foreach (var (address, view) in new[] { ("/Admin/Queue", "queue"), ("/Admin/Queue/History", "history"), ("/Admin/Wanted", "wanted") })
                    {
                        await page.GotoAsync(address);
                        await Assertions.Expect(page.Locator("main .list-body .list-row").First).ToBeVisibleAsync();
                        await Evidence.SaveAsync(page, "queue-nothing-to-assign-" + view + "-" + name);
                    }
                }
            }
            catch
            {
                await Evidence.SaveAsync(page, "queue-nothing-to-assign-failed");
                throw;
            }
        }
    }
}
