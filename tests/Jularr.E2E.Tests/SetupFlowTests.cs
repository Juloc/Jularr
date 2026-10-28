using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // The first-run flow after the owner's mockup: the first choice and its three branches, the storage step, the takeover step against a Sonarr, the steps in their order
    // with the overview, and the way back into a setup that was left unfinished.
    [TestClass]
    public sealed class SetupFlowTests
    {
        private static readonly string[] StepsInOrder = ["Owner & language", "Start", "Storage", "Modules", "Quality profiles", "Libraries", "Requests", "Download storage", "Download clients", "Indexers", "Providers", "Overview"];

        [TestMethod]
        [DataRow(1440, 900)]
        [DataRow(390, 844)]
        [Timeout(300_000)]
        public async Task FirstChoice_NewTakeoverAndRestore_EachLeadsItsOwnWay(int width, int height)
        {
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Backup:PgRestorePath"] = "pg_restore" });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await page.SetViewportSizeAsync(width, height);
            try
            {
                await Owner.CreateAsync(page);
                await page.WaitForURLAsync("**/Setup/Begin");

                // Three tiles and no page heading; the steps of the flow stand at the top and nothing of the application's menus.
                await Assertions.Expect(page.Locator(".setup-choice")).ToHaveCountAsync(3);
                await Assertions.Expect(page.Locator(".stepper__step")).ToHaveCountAsync(StepsInOrder.Length);
                await Assertions.Expect(page.Locator("main h1, main h2")).ToHaveCountAsync(0);
                await AssertStandaloneAsync(page);
                await Evidence.SaveAsync(page, "setup-begin-" + width);

                // Restoring a backup is its own page, standalone, with the way back to the choice.
                await page.GetByRole(AriaRole.Link, new() { Name = "Restore a backup", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Backup");
                await AssertStandaloneAsync(page);
                await page.GetByRole(AriaRole.Link, new() { Name = "Back", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Begin");

                // Taking over adds the Takeover step after the storage.
                await page.GetByRole(AriaRole.Button, new() { Name = "Take over from Sonarr, Radarr, Readarr or Lidarr", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Storage");
                await Assertions.Expect(page.Locator(".stepper__step")).ToHaveCountAsync(StepsInOrder.Length + 1);
                await Assertions.Expect(page.Locator(".stepper__label", new() { HasText = "Takeover" })).ToHaveCountAsync(1);

                // A new setup takes it away again.
                await page.GetByRole(AriaRole.Link, new() { Name = "Back", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "New setup", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Storage");
                await Assertions.Expect(page.Locator(".stepper__step")).ToHaveCountAsync(StepsInOrder.Length);
                Assert.AreEqual((short)1, await jularr.ScalarAsync("SELECT \"SetupStartTypeId\" FROM \"InstanceSetupState\""));
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"), "The setup overflows its viewport.");
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "setup-first-choice-" + width);
                throw;
            }
        }

        [TestMethod]
        [DataRow(1440, 900)]
        [DataRow(390, 844)]
        [Timeout(300_000)]
        public async Task StorageStep_ListsTheMountedPathsAndTheAddDialogCanBeCancelled(int width, int height)
        {
            using var folders = new Folders("Series");
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string> { ["Storage:LocalPaths:0"] = folders.Library });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await page.SetViewportSizeAsync(width, height);
            try
            {
                await Owner.CreateAsync(page);
                await page.GotoAsync("/Setup/Storage");

                // The folder the installation offers is a tile of its own, without typing anything; one click connects it and the connection is looked at at once.
                var available = page.Locator(".feature-card", new() { HasText = "Available" });
                await Assertions.Expect(available).ToHaveCountAsync(1);
                await Assertions.Expect(available).ToContainTextAsync(folders.Library);
                await Evidence.SaveAsync(page, "setup-storage-" + width);
                await available.GetByRole(AriaRole.Button, new() { Name = "Connect", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".feature-card", new() { HasText = "Available" })).ToHaveCountAsync(0);
                var connected = page.Locator(".feature-card", new() { HasText = "library" }).First;
                await Assertions.Expect(connected).ToContainTextAsync("Online");

                // A new connection (SMB, NFS or a local path) is added in a dialog that can be cancelled.
                var dialog = page.Locator("dialog#storage-add-Smb");
                await Assertions.Expect(dialog).Not.ToBeVisibleAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Add SMB connection", Exact = true }).ClickAsync();
                await Assertions.Expect(dialog).ToBeVisibleAsync();
                await Assertions.Expect(dialog.Locator("[name=Host]")).ToBeVisibleAsync();
                await Evidence.SaveAsync(page, "setup-storage-dialog-" + width);
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                await Assertions.Expect(dialog).Not.ToBeVisibleAsync();
                Assert.AreEqual(1L, await jularr.ScalarAsync("SELECT count(*) FROM \"StorageMounts\""), "Cancelling must add nothing.");
                await AssertStandaloneAsync(page);
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "setup-storage-failed-" + width);
                throw;
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task TakeoverStep_ConnectsReadsAndShowsWhatItFoundThenTakesOver()
        {
            using var folders = new Folders("Series");
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Tvdb"] = fakes.Tvdb4.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.CreateAsync(page);
                await page.GetByRole(AriaRole.Button, new() { Name = "Take over from Sonarr, Radarr, Readarr or Lidarr", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Storage");

                // The storage comes first: it is needed to put the libraries of a source somewhere.
                await page.Locator(".feature-card", new() { HasText = "Available" }).GetByRole(AriaRole.Button, new() { Name = "Connect", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".feature-card", new() { HasText = "Available" })).ToHaveCountAsync(0);
                await page.GetByRole(AriaRole.Button, new() { Name = "Continue", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Takeover");

                // Titles are identified through TMDB, so its key is asked for right in the step.
                var tmdb = page.Locator(".setup-source", new() { HasText = "TMDB" });
                await tmdb.Locator("[name=Secret]").FillAsync(FakeServices.TmdbToken);
                await tmdb.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".setup-source", new() { HasText = "TMDB" })).ToHaveCountAsync(0);

                // A wrong key is answered in plain words, in the card of that system only.
                var sonarr = page.Locator(".setup-source", new() { HasText = "Sonarr" });
                await sonarr.Locator("[name=BaseUrl]").FillAsync(fakes.Sonarr.ToString());
                await sonarr.Locator("[name=ApiKey]").FillAsync("wrong-key");
                await sonarr.GetByRole(AriaRole.Button, new() { Name = "Preview", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".setup-source", new() { HasText = "refused the API key" })).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator(".setup-source", new() { HasText = "Sonarr" }).First).ToContainTextAsync("refused the API key");

                // The wrong key was answered with the service's own 503, the one expected problem.
                Assert.IsTrue(browser.Problems.All(problem => problem.Contains("503", StringComparison.Ordinal)), string.Join(Environment.NewLine, browser.Problems));
                browser.Problems.Clear();

                // The preview counts what is there and changes nothing in Jularr.
                var profilesBefore = await jularr.ScalarAsync("SELECT count(*) FROM \"AcquisitionProfiles\"");
                await sonarr.Locator("[name=BaseUrl]").FillAsync(fakes.Sonarr.ToString());
                await sonarr.Locator("[name=ApiKey]").FillAsync(FakeServices.SonarrKey);
                await sonarr.GetByRole(AriaRole.Button, new() { Name = "Preview", Exact = true }).ClickAsync();
                sonarr = page.Locator(".setup-source", new() { HasText = "Sonarr" });
                await Assertions.Expect(sonarr).ToContainTextAsync("Preview ready");
                foreach (var kind in new[] { "Quality profiles", "Custom formats", "Series", "Root folders" })
                {
                    await Assertions.Expect(sonarr.Locator(".kpi-tile", new() { HasText = kind })).ToHaveCountAsync(1);
                }

                await Assertions.Expect(sonarr.Locator(".setup-source__fold", new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"^\s*Quality profiles") })).ToContainTextAsync("1");
                Assert.AreEqual(profilesBefore, await jularr.ScalarAsync("SELECT count(*) FROM \"AcquisitionProfiles\""), "A preview writes nothing.");
                // Discover feeds may store catalog titles; the preview adds nothing to the library.
                Assert.AreEqual(0L, await jularr.ScalarAsync("SELECT count(DISTINCT \"WorkId\") FROM \"WorkLibraryRelations\""));
                await Evidence.SaveAsync(page, "setup-takeover-preview");

                // The root folder needs a library: the form offers to create it on the connected storage; then the takeover brings the profile and the series.
                await sonarr.Locator("[name='NewLibraries[0].StorageMountId']").SelectOptionAsync(new SelectOptionValue { Index = 1 });
                await sonarr.Locator("[name='NewLibraries[0].RelativePath']").FillAsync("Series");
                await sonarr.GetByRole(AriaRole.Button, new() { Name = "Take over", Exact = true }).ClickAsync();
                sonarr = page.Locator(".setup-source", new() { HasText = "Sonarr" });
                await Assertions.Expect(sonarr).ToContainTextAsync("Taken over");
                Assert.AreEqual(1L, await jularr.ScalarAsync("SELECT count(DISTINCT \"WorkId\") FROM \"WorkLibraryRelations\""));
                Assert.AreEqual(1L, await jularr.ScalarAsync("SELECT count(*) FROM \"LibraryRoots\""));
                Assert.AreEqual(0L, await jularr.ScalarAsync("SELECT count(*) FROM \"AcquisitionConnections\" WHERE \"IsEnabled\" AND \"AcquisitionConnectionTypeId\" = 2"), "A takeover starts no download client.");
                Assert.AreEqual(0, fakes.Grabbed.Count, "A takeover starts no downloads.");
                await Evidence.SaveAsync(page, "setup-takeover-done");

                // The later steps start from what was taken over: the quality profile and the library are there.
                await page.GetByRole(AriaRole.Button, new() { Name = "Continue", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Instance");
                await page.GotoAsync("/Setup/Profiles?MediaTypeId=Series");
                await Assertions.Expect(page.Locator(".list-row", new() { HasText = "Web 1080p" })).ToHaveCountAsync(1);
                await page.GotoAsync("/Setup/Libraries");
                await Assertions.Expect(page.Locator(".setup-libraries__root", new() { HasText = "Series" })).ToHaveCountAsync(1);
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "setup-takeover-failed");
                throw;
            }
        }

        [TestMethod]
        [DataRow(1440, 900)]
        [DataRow(390, 844)]
        [Timeout(600_000)]
        public async Task Flow_EveryTopicIsAStepInOrder_AndTheOverviewFinishes(int width, int height)
        {
            using var folders = new Folders("Movies", "Series");
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Tvdb"] = fakes.Tvdb4.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            await page.SetViewportSizeAsync(width, height);
            try
            {
                await Owner.CreateAsync(page);
                await page.GetByRole(AriaRole.Button, new() { Name = "New setup", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Storage");

                // The steps are listed once, in this order, and never as tabs inside a step.
                var labels = await page.Locator(".stepper__label").AllTextContentsAsync();
                CollectionAssert.AreEqual(StepsInOrder, labels.ToArray());
                await page.Locator(".feature-card", new() { HasText = "Available" }).GetByRole(AriaRole.Button, new() { Name = "Connect", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".feature-card", new() { HasText = "Available" })).ToHaveCountAsync(0);
                await NextAsync(page, "/Setup/Instance");

                // Modules: the owner's choices of the merged step stay; the next step is the quality profiles.
                await page.Locator("[name=Name]").FillAsync("Flow test");
                await page.GetByRole(AriaRole.Button, new() { Name = "Continue", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Profiles");
                await AssertNoTabsAsync(page);
                await NextAsync(page, "/Setup/Libraries");

                // Libraries: a folder is chosen for a media type on the connection and added; the media without one go off.
                var movies = page.Locator(".setup-libraries__row", new() { HasText = "Movies" });
                await movies.GetByRole(AriaRole.Link, new() { Name = "Browse folders" }).ClickAsync();
                var picker = page.GetByRole(AriaRole.Dialog, new() { Name = "Browse folders", Exact = true });
                await picker.GetByRole(AriaRole.Link, new() { Name = "library", Exact = false }).First.ClickAsync();
                await picker.GetByRole(AriaRole.Link, new() { Name = "Movies", Exact = true }).ClickAsync();
                await picker.GetByRole(AriaRole.Button, new() { Name = "Use this folder", Exact = true }).ClickAsync();
                await movies.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Libraries");
                await Assertions.Expect(page.Locator(".setup-libraries__row", new() { HasText = "Movies" }).Locator(".setup-libraries__root")).ToHaveCountAsync(1);
                await NextAsync(page, "/Setup/Requests");
                await NextAsync(page, "/Setup/DownloadStorage", "Continue");
                await NextAsync(page, "/Setup/Downloads");
                await AssertNoTabsAsync(page);
                await NextAsync(page, "/Setup/Indexers");
                await AssertNoTabsAsync(page);

                // Only adding opens a dialog (with Cancel); the added indexer is the same card as in the administration, its settings as inputs inside it.
                await Steps.FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Add indexer", Exact = true }).And(page.Locator("[href*='Type=Newznab']")));
                await Assertions.Expect(page.Locator("dialog#connection-editor").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true })).ToHaveCountAsync(1);
                var indexerForm = page.Locator("dialog#connection-editor form").First;
                await indexerForm.Locator("[name=Name]").FillAsync("Setup indexer");
                await indexerForm.Locator("[name=BaseUrl]").FillAsync(new Uri(fakes.Address, "newznab/").ToString());
                await indexerForm.Locator("[name=ApiKey]").FillAsync(FakeServices.IndexerKey);
                await Steps.SubmitAsync(page, indexerForm);
                var indexerCard = page.Locator(".card-grid .feature-card", new() { HasText = "Setup indexer" });
                await Assertions.Expect(indexerCard.Locator("[name=BaseUrl]")).ToBeVisibleAsync();
                await Assertions.Expect(indexerCard.GetByRole(AriaRole.Button, new() { Name = "Test connection", Exact = true })).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
                await NextAsync(page, "/Setup/Providers");

                // Providers: every provider is a card with its key right in it, saved together.
                var tmdb = page.Locator(".feature-card", new() { HasText = "TMDB" });
                await tmdb.Locator("input[type=password]").FillAsync(FakeServices.TmdbToken);
                await Assertions.Expect(page.Locator("dialog[open]")).ToHaveCountAsync(0);
                await page.GetByRole(AriaRole.Button, new() { Name = "Continue", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Overview");
                Assert.AreEqual(true, await jularr.ScalarAsync("SELECT \"ProtectedSecret\" IS NOT NULL FROM \"Providers\" WHERE \"Key\" = 'tmdb'"));

                // The overview has a row for every step with the way back to it; the one thing missing (a download client) is accepted with Finish anyway, which closes the setup and opens the application.
                await Assertions.Expect(page.Locator(".setup-overview__row")).ToHaveCountAsync(StepsInOrder.Length - 2);
                await Assertions.Expect(page.Locator(".setup-overview__row a[href='/Setup/Libraries']")).ToHaveCountAsync(1);
                Assert.IsFalse(await page.Locator("body").EvaluateAsync<bool>("body => body.scrollWidth > innerWidth + 1"), "The overview overflows its viewport.");
                await Evidence.SaveAsync(page, "setup-overview-" + width);
                await Assertions.Expect(Row(page, "Indexers")).ToHaveAttributeAsync("data-state", "ready");
                await Assertions.Expect(Row(page, "Download clients")).ToHaveAttributeAsync("data-state", "missing");
                await page.GetByRole(AriaRole.Button, new() { Name = "Finish anyway", Exact = true }).ClickAsync();
                await page.Locator("dialog#finish-anyway").GetByRole(AriaRole.Button, new() { Name = "Finish anyway", Exact = true }).ClickAsync();
                await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
                await page.GotoAsync("/Setup/Storage");
                await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
                await page.GotoAsync("/Setup");
                await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
                CollectionAssert.AreEqual(Array.Empty<string>(), browser.Problems.ToArray());
            }
            catch
            {
                await Evidence.SaveAsync(page, "setup-flow-failed-" + width);
                throw;
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task ClickingThroughWithNothingEntered_MarksSkippedStepsAndTheOverviewSaysWhatIsMissing_FinishAnywayAsks()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.CreateAsync(page);
                await page.GetByRole(AriaRole.Button, new() { Name = "New setup", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Storage");
                await NextAsync(page, "/Setup/Instance");
                await NextAsync(page, "/Setup/Profiles");
                await NextAsync(page, "/Setup/Libraries");
                await NextAsync(page, "/Setup/Requests");
                await NextAsync(page, "/Setup/DownloadStorage");
                await NextAsync(page, "/Setup/Downloads");
                await NextAsync(page, "/Setup/Indexers");
                await NextAsync(page, "/Setup/Providers");

                // AniList's catalog needs no key: its card has no inputs without the AniList module, and with it the pair the profiles sign in with.
                var anilist = page.Locator(".setup-providers .feature-card", new() { HasText = "AniList" });
                await Assertions.Expect(anilist.Locator("input[type=password], input[type=text]")).ToHaveCountAsync(0);
                var igdb = page.Locator(".setup-providers .feature-card", new() { HasText = "IGDB" });
                await Assertions.Expect(igdb.Locator("input[type=password], input[type=text]")).ToHaveCountAsync(2);
                await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'AniList'");
                await page.ReloadAsync();
                await Assertions.Expect(anilist.Locator("input[type=password], input[type=text]")).ToHaveCountAsync(2);
                await Assertions.Expect(anilist.GetByText("Client ID for the profiles' sign-in")).ToHaveCountAsync(1);
                await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'AniList'");
                await NextAsync(page, "/Setup/Overview");

                // Nothing was entered: storage, libraries and the Arr+ connections are missing, the steps left empty carry the skipped marker, the others the check.
                var storage = Row(page, "Storage");
                await Assertions.Expect(storage).ToHaveAttributeAsync("data-state", "missing");
                await Assertions.Expect(storage).ToContainTextAsync("No storage connection yet");
                await Assertions.Expect(Row(page, "Indexers")).ToHaveAttributeAsync("data-state", "missing");
                await Assertions.Expect(Row(page, "Download clients")).ToHaveAttributeAsync("data-state", "missing");
                await Assertions.Expect(Row(page, "Libraries")).ToHaveAttributeAsync("data-state", "skipped");
                await Assertions.Expect(Row(page, "Start").And(page.Locator("[data-state=ready]"))).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator(".setup-overview__row a[href='/Setup/Storage']")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator(".stepper__step--skipped .stepper__label", new() { HasText = "Storage" })).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator(".stepper__step--skipped .stepper__label", new() { HasText = "Indexers" })).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator(".stepper__step--done .stepper__label", new() { HasText = "Modules" })).ToHaveCountAsync(1);

                await Evidence.SaveAsync(page, "setup-honest-overview");

                // Finishing is still possible, but only after saying so: Cancel keeps the setup open, the confirmation closes it.
                await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Finish setup", Exact = true })).ToHaveCountAsync(0);
                await page.GetByRole(AriaRole.Button, new() { Name = "Finish anyway", Exact = true }).ClickAsync();
                var dialog = page.Locator("dialog#finish-anyway");
                await Assertions.Expect(dialog).ToBeVisibleAsync();
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
                await Assertions.Expect(dialog).ToBeHiddenAsync();
                Assert.AreEqual(false, await jularr.ScalarAsync("SELECT \"CompletedAt\" IS NOT NULL FROM \"InstanceSetupState\""));
                await page.GetByRole(AriaRole.Button, new() { Name = "Finish anyway", Exact = true }).ClickAsync();
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Finish anyway", Exact = true }).ClickAsync();
                await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
                Assert.AreEqual(true, await jularr.ScalarAsync("SELECT \"CompletedAt\" IS NOT NULL FROM \"InstanceSetupState\""));
            }
            catch
            {
                await Evidence.SaveAsync(page, "setup-honest-overview-failed");
                throw;
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task Providers_AniListKeyFieldsAppearOnlyWithTheModule_InTheAdministrationToo()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await page.GotoAsync("/Admin/Providers");
                var anilist = page.Locator("#provider-anilist");
                await Assertions.Expect(anilist).ToBeVisibleAsync();
                await Assertions.Expect(anilist.Locator("[name=ClientId], [name=Secret]")).ToHaveCountAsync(0);
                await Assertions.Expect(anilist).ToContainTextAsync("Enabled");
                await Assertions.Expect(page.Locator("#provider-igdb [name=ClientId], #provider-igdb [name=Secret]")).ToHaveCountAsync(2);

                await jularr.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" IN ('AniList')");
                await page.ReloadAsync();
                await Assertions.Expect(anilist.Locator("[name=ClientId], [name=Secret]")).ToHaveCountAsync(2);
                await Assertions.Expect(anilist.GetByText("Client secret for the profiles' sign-in")).ToHaveCountAsync(1);
                await Assertions.Expect(anilist).ToContainTextAsync("Setup needed");
            }
            catch
            {
                await Evidence.SaveAsync(page, "setup-anilist-fields-failed");
                throw;
            }
        }

        [TestMethod]
        [Timeout(300_000)]
        public async Task UnfinishedSetup_EveryAddressLeadsBackToTheSavedStep_AndOthersAreToldItIsBeingSetUp()
        {
            await using var jularr = await JularrInstance.StartAsync();
            await using var browser = await BrowserSession.StartAsync(jularr);
            var page = browser.Page;
            try
            {
                // Before anyone exists every address leads to the Owner step.
                await page.GotoAsync("/Library");
                await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/Setup");
                await Owner.CreateAsync(page);

                // Two steps are done; the owner leaves (the tab is closed).
                await page.GetByRole(AriaRole.Button, new() { Name = "New setup", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Storage");
                await NextAsync(page, "/Setup/Instance");
                await page.Locator("[name=Name]").FillAsync("Saved name");
                await page.GetByRole(AriaRole.Button, new() { Name = "Continue", Exact = true }).ClickAsync();
                await page.WaitForURLAsync("**/Setup/Profiles");
                await page.CloseAsync();

                // Back later: the root and deep addresses of the application lead to the step last reached, and what was entered is still there.
                var again = await browser.Context.NewPageAsync();
                foreach (var address in new[] { "/", "/Admin", "/Library", "/Admin/Storage" })
                {
                    await again.GotoAsync(address);
                    await again.WaitForURLAsync("**/Setup/Profiles");
                }

                await again.GotoAsync("/Setup/Instance");
                await Assertions.Expect(again.Locator("[name=Name]")).ToHaveValueAsync("Saved name");

                // Signed out, the Owner signs in again and arrives at the same step.
                await again.GotoAsync("/Account/Logout");
                await again.Locator("main form button[type=submit]").ClickAsync();
                await again.GotoAsync("/Library");
                await again.WaitForURLAsync("**/Account/Login**");
                await again.FillAsync("[name=Username]", Owner.Username);
                await again.FillAsync("[name=Password]", Owner.Password);
                await again.Locator("form button[type=submit]").First.ClickAsync();
                await again.WaitForURLAsync("**/Setup/Profiles");

                // Another account only sees that the instance is being set up.
                await jularr.RunSqlAsync("""
                    INSERT INTO "Accounts" ("Username", "DisplayName", "AccountRoleTypeId") VALUES ('second_user', 'Second', 2);
                    INSERT INTO "AccountPasswords" ("AccountId", "PasswordHash")
                    SELECT second."Id", password."PasswordHash" FROM "Accounts" AS second, "AccountPasswords" AS password JOIN "Accounts" AS owner ON owner."Id" = password."AccountId"
                    WHERE second."Username" = 'second_user' AND owner."Username" = 'owner_e2e';
                    INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId") SELECT second."Id", 'Second', owner_profile."UiLocaleId"
                    FROM "Accounts" AS second, "Profiles" AS owner_profile WHERE second."Username" = 'second_user' LIMIT 1;
                    INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") SELECT profile."OwnerAccountId", profile."Id" FROM "Profiles" AS profile WHERE profile."DisplayName" = 'Second';
                    """);
                await again.GotoAsync("/Account/Logout");
                await again.Locator("main form button[type=submit]").ClickAsync();
                await again.GotoAsync("/Account/Login");
                await again.FillAsync("[name=Username]", "second_user");
                await again.FillAsync("[name=Password]", Owner.Password);
                await again.Locator("form button[type=submit]").First.ClickAsync();
                await again.WaitForURLAsync("**/Setup/Waiting");
                await Assertions.Expect(again.Locator("body")).ToContainTextAsync("being set up");
                await again.GotoAsync("/Library");
                await again.WaitForURLAsync("**/Setup/Waiting");
                Assert.AreEqual(0, await again.Locator(".stepper").CountAsync(), "Someone else does not see the steps.");
                await AssertStandaloneAsync(again);
            }
            catch
            {
                await Evidence.SaveAsync(browser.Context.Pages[^1], "setup-resume-failed");
                throw;
            }
        }

        // A step's row of the overview, found by its name at the start.
        private static ILocator Row(IPage page, string name) => page.Locator(".setup-overview__row", new() { HasTextRegex = new Regex("^\\s*" + Regex.Escape(name)) });

        // The Next button of a step without a form of its own; the step after it opens.
        private static async Task NextAsync(IPage page, string next, string label = "Continue")
        {
            await page.GetByRole(AriaRole.Button, new() { Name = label, Exact = true }).Last.ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == next);
        }

        // Setup pages stand alone: the application's sidebar and header are not there.
        private static async Task AssertStandaloneAsync(IPage page)
        {
            Assert.AreEqual(0, await page.Locator(".app-sidebar, .app-header, [data-shell=app]").CountAsync(), "The setup shows the application's menus.");
        }

        private static async Task AssertNoTabsAsync(IPage page)
        {
            Assert.AreEqual(0, await page.Locator(".navigation-tabs, [role=tablist]").CountAsync(), "A setup step shows tabs.");
        }
    }
}
