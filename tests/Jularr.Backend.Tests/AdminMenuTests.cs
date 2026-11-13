using System.Net;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Public.Authentication.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class AdminMenuTests
    {
        [TestMethod]
        public async Task Sidebar_ListsEntriesByModuleAndRight_AndKeepsEveryOldAddress()
        {
            await using var host = await CreateAsync();
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" IN ('Acquisition', 'Requests')");

            // The Owner with the modules on sees every entry once; each links to the first of its pages.
            var html = await GetAsync(host, "/Admin/Dashboard");
            var entries = Entries(html);
            foreach (var address in new[] { "/Admin/Dashboard", "/Admin/Queue", "/Admin/Wanted", "/Admin/Jobs/Tasks", "/Admin/Works", "/Admin/Library", "/Admin/Downloads", "/Admin/Downloads/Servers", "/Admin/Downloads/Speed",
                "/Admin/Downloads/Clients", "/Admin/Acquisition", "/Admin/Acquisition/Profiles?tab=profiles", "/Admin/Acquisition/Sources", "/Admin/Providers", "/Admin/Storage", "/Admin/Accounts", "/Admin/Instance", "/Admin/Localization", "/Admin/System", "/Admin/Backup", "/Admin/Ai" })
            {
                CollectionAssert.Contains(entries, address);
            }

            Assert.AreEqual(21, entries.Count);
            Assert.AreEqual(7, Regex.Matches(html, "<section class=\"menu-group").Count);
            StringAssert.Contains(html, "class=\"menu-entry is-active\" href=\"/Admin/Dashboard\"");

            // Downloads is a group: each of its pages is an entry of its own, so none has a chip row; the indexers keep theirs, without the former download clients page.
            foreach (var address in new[] { "/Admin/Downloads", "/Admin/Downloads/Servers", "/Admin/Downloads/Speed", "/Admin/Downloads/Clients" })
            {
                var page = await GetAsync(host, address);
                Assert.IsFalse(page.Contains("navigation-tabs--filters", StringComparison.Ordinal), address);
                StringAssert.Contains(page, "class=\"menu-entry is-active\" href=\"" + address + "\"", address);
            }

            // The quality profiles are an entry of their own: their five tabs are its chips.
            var rules = Row(await GetAsync(host, "/Admin/Acquisition/Profiles"));
            foreach (var pane in new[] { "profiles", "formats", "groups", "releases", "quality" }) StringAssert.Contains(rules, "tab=" + pane);
            foreach (var pane in new[] { "sizes", "release\"", "tags" }) Assert.IsFalse(rules.Contains("tab=" + pane, StringComparison.Ordinal), pane);
            Assert.IsFalse(rules.Contains("href=\"/Admin/Acquisition\"", StringComparison.Ordinal));

            // A module that is off takes its entries and chips with it.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" IN ('Acquisition', 'Requests')");
            entries = Entries(await GetAsync(host, "/Admin/Dashboard"));
            foreach (var address in new[] { "/Admin/Downloads", "/Admin/Downloads/Servers", "/Admin/Downloads/Speed", "/Admin/Downloads/Clients", "/Admin/Acquisition", "/Admin/Acquisition/Profiles?tab=profiles", "/Admin/Acquisition/Sources", "/Admin/Wanted" })
            {
                CollectionAssert.DoesNotContain(entries, address);
            }

            // The queue entry loses its Blocked page, the users entry its requests: only the Accounts page is left, so no row of siblings. Wanted is an entry of its own, not a page of the Queue.
            var queue = await GetAsync(host, "/Admin/Queue");
            Assert.IsFalse(queue.Contains("href=\"/Admin/Queue/Blocked\"", StringComparison.Ordinal));
            var accounts = await GetAsync(host, "/Admin/Accounts");
            Assert.IsFalse(accounts.Contains("navigation-tabs--filters", StringComparison.Ordinal), "An entry with one page has no chip row.");

            // A media manager has no administration rights: no dashboard, jobs, settings or system entries.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            var manager = Entries(await GetAsync(host, "/Admin/Queue"));
            foreach (var address in new[] { "/Admin/Dashboard", "/Admin/Jobs/Tasks", "/Admin/Instance", "/Admin/System", "/Admin/Backup" })
            {
                CollectionAssert.DoesNotContain(manager, address);
            }

            CollectionAssert.Contains(manager, "/Admin/Queue");
            CollectionAssert.Contains(manager, "/Admin/Works");
        }

        [TestMethod]
        public async Task Pages_OfOneEntry_ShareOneChipRow_WithTheirOwnMarked()
        {
            await using var host = await CreateAsync();

            // Settings: five pages, one row, the page's own chip marked on each.
            foreach (var (address, current) in new[]
            {
                ("/Admin/Instance", "/Admin/Instance"), ("/Admin/Instance/Modules", "/Admin/Instance/Modules"), ("/Admin/Instance/MediaTypes", "/Admin/Instance/MediaTypes"),
                ("/Admin/Instance/Languages", "/Admin/Instance/Languages"), ("/Admin/Instance/Naming", "/Admin/Instance/Naming")
            })
            {
                var html = await GetAsync(host, address);
                Assert.AreEqual(1, Regex.Matches(html, "class=\"navigation-tabs navigation-tabs--filters\"").Count, address);
                var row = Row(html);
                StringAssert.Contains(row, "href=\"/Admin/Instance/Modules\"");
                StringAssert.Contains(row, "href=\"" + current + "\" class=\"navigation-tabs__link navigation-tabs__link--active\"", address);
            }

            // The system pages, the jobs and the library share the same kind of row.
            foreach (var (address, sibling) in new[] { ("/Admin/Logs", "/Admin/System"), ("/Admin/Transcoding", "/Admin/Jobs"), ("/Admin/Jobs/Tasks", "/Admin/Jobs"), ("/Admin/Queue/History", "/Admin/Queue"),
                ("/Admin/Library/Files", "/Admin/Library/Unmatched") })
            {
                var html = await GetAsync(host, address);
                StringAssert.Contains(Row(html), "href=\"" + sibling + "\"", address);
            }

            // The jobs' schedule merges its filter chips into the same row, after a divider; the entry's Tasks come first.
            var jobs = Row(await GetAsync(host, "/Admin/Jobs"));
            Assert.IsTrue(jobs.IndexOf("href=\"/Admin/Jobs/Tasks\"", StringComparison.Ordinal) is >= 0 and var tasks && tasks < jobs.IndexOf("href=\"/Admin/Jobs\"", StringComparison.Ordinal), "Tasks, then the schedule.");
            StringAssert.Contains(jobs, "href=\"/Admin/Transcoding\"");
            StringAssert.Contains(jobs, "navigation-tabs__divider");
            StringAssert.Contains(jobs, "href=\"/Admin/Jobs?state=failed\"");

            // The quality rules are tabs of one address, none of them about one media type.
            var formats = Row(await GetAsync(host, "/Admin/Acquisition/Profiles?tab=formats"));
            StringAssert.Contains(formats, "href=\"/Admin/Acquisition/Profiles?tab=formats\" class=\"navigation-tabs__link navigation-tabs__link--active\"");
            StringAssert.Contains(formats, "href=\"/Admin/Acquisition/Profiles?tab=quality\"");

            // A detail page of an entry has no row of its own.
            await host.Fixture.RunSqlAsync("INSERT INTO \"Works\" (\"Id\", \"MediaTypeId\", \"CanonicalTitle\") VALUES (10, 1, 'Film')");
            Assert.IsFalse((await GetAsync(host, "/Admin/Works/10")).Contains("navigation-tabs--filters", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Sidebar_ShowsTheThreeFigures_WhereTheyBelong_AndHidesZero()
        {
            await using var host = await CreateAsync();
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" IN ('Acquisition', 'Requests')");

            // Nothing waits: the badges exist for the stream, but hide.
            var quiet = await GetAsync(host, "/Admin/Dashboard");
            foreach (var name in new[] { "downloads", "jobs", "requests" })
            {
                StringAssert.Matches(quiet, new Regex("<span class=\"menu-badge\" data-live=\"menu\" data-live-text=\"" + name + "\" hidden=\"\">0</span>"));
            }

            // An open request, a failed job's notice and a running download show on their entries (and only there).
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (10, 1, 'Film'), (11, 1, 'Other film');
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId", "AcquisitionRequestStatusTypeId")
                VALUES (500, 10, 101, 41, 1, 1), (501, 11, 101, 41, 1, 1);
                INSERT INTO "WorkerJobs" ("Key", "LastResultTypeId", "LastErrorKey", "LastFinishedAt") VALUES ('downloads', 2, 'job_failed', now());
                INSERT INTO "Operations" ("OperationTypeId", "OperationStatusTypeId", "WorkerJobKey", "ErrorKey", "FinishedAt") VALUES (4, 4, 'downloads', 'job_failed', now());
                """);
            var waiting = await GetAsync(host, "/Admin/Dashboard");
            StringAssert.Contains(waiting, "data-live-text=\"requests\">2</span>");
            StringAssert.Contains(waiting, "data-live-text=\"jobs\">1</span>");
            StringAssert.Contains(waiting, "data-live-text=\"downloads\" hidden=\"\">0</span>");

            // A media manager reads downloads but neither jobs nor requests of the administration.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            var manager = await GetAsync(host, "/Admin/Queue");
            StringAssert.Contains(manager, "data-live-text=\"downloads\"");
            Assert.IsFalse(manager.Contains("data-live-text=\"jobs\"", StringComparison.Ordinal));
        }

        // The queue, its history, its blocked releases and the manual import moved to the Queue entry: the old addresses (notifications, bookmarks) lead there with their choices.
        [TestMethod]
        public async Task OldActivityAddresses_LeadToTheQueue()
        {
            await using var host = await CreateAsync();
            foreach (var (old, now) in new[] { ("/Admin/Activity?operation=5&type=movies", "/Admin/Queue?operation=5&type=movies"), ("/Admin/Activity?Filter=Waiting", "/Admin/Queue?Filter=Waiting"),
                ("/Admin/History?WorkId=10", "/Admin/Queue/History?WorkId=10"), ("/Admin/Activity/Blocked?type=books", "/Admin/Queue/Blocked?type=books"),
                ("/Admin/ManualImport?operationId=7", "/Admin/Queue?import=true&importOperation=7"), ("/Admin/ManualImport", "/Admin/Queue?import=true") })
            {
                using var response = await host.SendAsync(HttpMethod.Get, old);
                Assert.AreEqual(HttpStatusCode.MovedPermanently, response.StatusCode, old);
                Assert.AreEqual(now, response.Headers.Location!.OriginalString, old);
            }

            // The former queue filter for finished downloads opens the history.
            using var history = await host.SendAsync(HttpMethod.Get, "/Admin/Queue?Filter=History");
            Assert.AreEqual("/Admin/Queue/History", history.Headers.Location!.OriginalString);
        }

        [TestMethod]
        public async Task UserMenu_HasOneLinkPerArea_FromTheSameMenu_ByModuleAndRight()
        {
            await using var host = await CreateAsync();
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" IN ('Acquisition', 'Requests')");

            // The Owner has one link per area, each to the first page of its entry, and no single Administration link.
            var html = await GetAsync(host, "/");
            var links = UserMenu(html);
            foreach (var address in new[] { "/Admin/Dashboard", "/Admin/Works", "/Admin/Library", "/Admin/Downloads", "/Admin/Accounts", "/Admin/System" })
            {
                CollectionAssert.Contains(links, address);
            }

            Assert.AreEqual(6, links.Count(link => link.StartsWith("/Admin", StringComparison.Ordinal)));

            // A module that is off takes its area with it; a media manager keeps the media areas only.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Acquisition'");
            CollectionAssert.DoesNotContain(UserMenu(await GetAsync(host, "/")), "/Admin/Downloads");
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 3 WHERE \"Id\" = 41");
            var manager = UserMenu(await GetAsync(host, "/"));
            CollectionAssert.Contains(manager, "/Admin/Works");
            CollectionAssert.DoesNotContain(manager, "/Admin/Dashboard");
            CollectionAssert.DoesNotContain(manager, "/Admin/System");
        }

        // The links of the user menu's second block.
        private static List<string> UserMenu(string html)
        {
            var start = html.IndexOf("sidebar-secondary", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "The page has no user menu.");
            return [.. Regex.Matches(html[start..html.IndexOf("</nav>", start, StringComparison.Ordinal)], "href=\"([^\"]+)\"").Select(match => match.Groups[1].Value)];
        }

        private static List<string> Entries(string html) => [.. Regex.Matches(html, "<a class=\"menu-entry[^\"]*\" href=\"([^\"]+)\"").Select(match => match.Groups[1].Value)];

        // The row of chips above the page.
        private static string Row(string html)
        {
            var start = html.IndexOf("class=\"navigation-tabs navigation-tabs--filters\"", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "The page has no row of chips.");
            return html[start..html.IndexOf("</nav>", start, StringComparison.Ordinal)];
        }

        private static async Task<string> GetAsync(Host host, string address)
        {
            using var response = await host.SendAsync(HttpMethod.Get, address);
            var html = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, address + ": " + html);
            return html;
        }

        private static async Task<Host> CreateAsync()
        {
            var host = await Host.CreateAsync(library: true, razor: true);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            return host;
        }
    }
}
