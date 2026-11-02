using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    [TestClass]
    public sealed class MediaImportDurabilityTests
    {
        // A real server process is killed after committed rows, while the original
        // browser tab is closed. A fresh tab must see the same persisted job and
        // its remaining work, never a second request or download.
        [TestMethod]
        [Timeout(300_000)]
        public async Task MoreThanThreeHundredWorks_SurviveClosedTabAndServerRestart_WithoutDuplicates()
        {
            const int count = 308;
            using var folders = new Folders("Movies");
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;
            await Owner.SetUpAndSignInAsync(page);
            await Steps.SetTmdbKeyAsync(page);
            await page.GotoAsync("/Admin/Import");

            var json = JsonSerializer.Serialize(new
            {
                version = 1,
                items = Enumerable.Range(30000, count).Select(id => new
                {
                    type = "movie",
                    ids = new { tmdb = id.ToString(CultureInfo.InvariantCulture) },
                    title = "Bulk Movie " + id.ToString(CultureInfo.InvariantCulture)
                })
            });
            var id = await page.EvaluateAsync<string>("""
                async json => {
                    const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
                    if (!token) throw new Error('Missing anti-forgery token');
                    const items = JSON.parse(json).items;
                    const choices = items.map((_, index) => ({
                        index, include: true, choice: null, query: null,
                        action: null, acquisitionProfileId: null, libraryRootId: null
                    }));
                    const response = await fetch('/api/v1/admin/media-import/jobs', {
                        method: 'POST', credentials: 'same-origin',
                        headers: { 'Content-Type': 'application/json', RequestVerificationToken: token },
                        body: JSON.stringify({ json, action: 1, defaults: [], choices })
                    });
                    if (!response.ok) throw new Error('Import enqueue failed: ' + response.status + ' ' + await response.text());
                    return (await response.json()).jobId;
                }
                """, json);
            Assert.IsTrue(Guid.TryParse(id, out _), "The job has a server-generated opaque identifier.");

            await page.CloseAsync();
            long committed = 0;
            for (var tries = 0; tries < 120; tries++)
            {
                committed = Convert.ToInt64(await jularr.ScalarAsync("SELECT \"Cursor\" FROM \"MediaImportJobs\" LIMIT 1;"), CultureInfo.InvariantCulture);
                if (committed > 0 && committed < count) break;
                await Task.Delay(250);
            }
            Assert.IsTrue(committed > 0 && committed < count, "The server must have committed some but not all rows before it is killed.");

            await jularr.RestartAsync();
            var returned = await session.Context.NewPageAsync();
            await returned.GotoAsync("/Admin/Import");
            var latest = await returned.EvaluateAsync<string>("""
                async () => {
                    const response = await fetch('/Admin/Import?handler=LatestJob', { credentials: 'same-origin', cache: 'no-store' });
                    if (!response.ok) throw new Error('Import job reconnect failed: ' + response.status);
                    return (await response.json()).jobId;
                }
                """);
            Assert.AreEqual(id, latest, "The new tab must recover the same owner-scoped job ID.");

            var completed = false;
            for (var tries = 0; tries < 210; tries++)
            {
                var status = await returned.EvaluateAsync<JsonElement>("""
                    async jobId => {
                        const response = await fetch('/Admin/Import?handler=Job&jobId=' + encodeURIComponent(jobId), {
                            credentials: 'same-origin', cache: 'no-store'
                        });
                        if (!response.ok) throw new Error('Import job status failed: ' + response.status);
                        return response.json();
                    }
                    """, id);
                if (status.GetProperty("state").GetString() == "Failed")
                    Assert.Fail("The resumed import failed: " + status.GetProperty("error").ToString());
                if (status.GetProperty("state").GetString() == "Completed")
                {
                    Assert.AreEqual((long)count, status.GetProperty("processed").GetInt64());
                    Assert.AreEqual(count, status.GetProperty("rows").GetArrayLength());
                    completed = true;
                    break;
                }
                await Task.Delay(1000);
            }

            Assert.IsTrue(completed, "A restarted server finishes the same durable import.");
            await Assertions.Expect(returned.Locator("[data-import-job-panel]")).ToBeVisibleAsync();
            await Assertions.Expect(returned.Locator("[data-import-job-progress]")).ToHaveJSPropertyAsync("value", count);
            Assert.AreEqual((long)count, Convert.ToInt64(await jularr.ScalarAsync("SELECT count(*) FROM \"Works\" WHERE \"CatalogNamespace\" IS NULL;"), CultureInfo.InvariantCulture));
            Assert.AreEqual(0L, Convert.ToInt64(await jularr.ScalarAsync("SELECT count(*) FROM \"WantedItems\";"), CultureInfo.InvariantCulture));
            Assert.AreEqual(0L, Convert.ToInt64(await jularr.ScalarAsync("SELECT count(*) FROM \"AcquisitionRequests\";"), CultureInfo.InvariantCulture));
        }
    }
}
