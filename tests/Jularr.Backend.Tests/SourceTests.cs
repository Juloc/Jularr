using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Account.V1;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Logic.AnnasArchive;
using Jularr.Integrations.Logic.Opds;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class SourceTests
    {
        private const string Connections = "/api/v1/admin/acquisition/connections";

        [TestMethod]
        public async Task Catalog_SetUpAddsKeylessSourcesAndAsksForAccounts()
        {
            using var storage = new TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path, razor: true);
            var before = await ReadCatalogAsync(host);
            Assert.IsFalse(before.Single(item => item.Key == "gutenberg").IsConfigured);
            Assert.IsTrue(before.Single(item => item.Key == "openlibrary").IsConfigured);
            Assert.AreEqual(SourceAvailability.ReferenceOnly, before.Single(item => item.Key == "bookwalker").SourceAvailabilityId);

            // Keys and accounts are asked for, never invented.
            using var setUp = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/acquisition/sources/setup");
            Assert.AreEqual(HttpStatusCode.OK, setUp.StatusCode, await setUp.Content.ReadAsStringAsync());
            var result = (await setUp.Content.ReadFromJsonAsync<SourceSetupResultV1>())!;
            CollectionAssert.AreEqual(new[] { "gutenberg" }, result.Added);
            CollectionAssert.IsSubsetOf(new[] { "annasarchive", "standardebooks", "newznab" }, result.NeedsAccount);
            Assert.IsTrue((await ReadCatalogAsync(host)).Single(item => item.Key == "gutenberg").IsConfigured);
            Assert.AreEqual(3L, await host.ScalarAsync("SELECT \"AcquisitionConnectionTypeId\"::bigint FROM \"AcquisitionConnections\""));

            using var page = await host.SendAsync(HttpMethod.Get, "/Admin/Acquisition/Sources");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "Reference only");
            StringAssert.Contains(html, "/Admin/Acquisition?Type=AnnasArchive");
        }

        [TestMethod]
        public async Task Opds_LogsInOnlyAtItsOwnOriginRenewsSignedLinksAndImports()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeSources();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Calibre", AcquisitionConnectionType.Opds,
                "https://opds.example.test/opds", FakeSources.Password, true, 40, [], null, null, null, FakeSources.User));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            using var listed = await host.SendAsync(HttpMethod.Get, Connections);
            Assert.IsFalse((await listed.Content.ReadAsStringAsync()).Contains(FakeSources.Password, StringComparison.Ordinal));
            var wantedId = await CreateWantedBookAsync(host);

            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            var candidates = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!;
            var opds = candidates.Single(candidate => candidate.ConnectionName == "Calibre" && candidate.QualityKey == "EPUB");
            Assert.AreEqual(ReleaseDecisionType.Eligible, opds.ReleaseDecisionTypeId);
            using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(opds.CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
            await RunAsync<MonitorDownloadsV1>(host);

            // The expired signed link was renewed once; the download host never saw the login.
            Assert.IsTrue(File.Exists(Path.Combine(storage.Path, "1", "Books", "Jane Austen", "Pride and Prejudice (1813)", "Pride and Prejudice.epub")));
            Assert.AreEqual(2, sources.SignedHits);
            Assert.IsFalse(sources.AuthorizationSeenBy.Contains("cdn.example.test"));
            Assert.IsTrue(sources.AuthorizationSeenBy.Contains("opds.example.test"));
        }

        [TestMethod]
        public async Task AnnasArchive_KeyStaysWithTheSiteAndGatesWaitForAPerson()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeSources { Challenge = true };
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Anna", AcquisitionConnectionType.AnnasArchive,
                "https://annas.example.test", FakeSources.MemberKey, true, 60, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var wantedId = await CreateWantedBookAsync(host);

            // A challenge is never passed in production; the source simply has no results.
            using var tested = await host.SendAsync(HttpMethod.Post, Connections + "/2/test");
            Assert.AreEqual("acquisition_unavailable", (await tested.Content.ReadFromJsonAsync<AcquisitionConnectionTestV1>())!.ErrorKey);
            using var blocked = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);

            // Without the challenge the member key goes to the API only, not to the file host.
            sources.Challenge = false;
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            var anna = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.Single(candidate => candidate.ConnectionName == "Anna");
            Assert.AreEqual(ReleaseDecisionType.Eligible, anna.ReleaseDecisionTypeId);
            using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(anna.CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.IsTrue(File.Exists(Path.Combine(storage.Path, "1", "Books", "Jane Austen", "Pride and Prejudice (1813)", "Pride and Prejudice.epub")));
            Assert.IsTrue(sources.Requests.Any(request => request.StartsWith("annas.example.test/dyn/api/fast_download.json", StringComparison.Ordinal) && request.Contains(FakeSources.MemberKey)));
            Assert.IsFalse(sources.Requests.Any(request => request.StartsWith("files.example.test", StringComparison.Ordinal) && request.Contains(FakeSources.MemberKey)));
        }

        [TestMethod]
        public async Task AnnasArchive_WithoutKeyKeepsTheSlowCountdownAndImports()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeSources();
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Anna", AcquisitionConnectionType.AnnasArchive,
                "https://annas.example.test", null, true, 60, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var wantedId = await CreateWantedBookAsync(host);
            await GrabAnnaAsync(host, wantedId);

            // The page's own countdown is waited out before the file is fetched.
            var started = DateTime.UtcNow;
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.IsTrue(DateTime.UtcNow - started >= TimeSpan.FromSeconds(1));
            Assert.IsTrue(File.Exists(Path.Combine(storage.Path, "1", "Books", "Jane Austen", "Pride and Prejudice (1813)", "Pride and Prejudice.epub")));
            Assert.IsTrue(sources.Requests.Any(request => request.StartsWith("annas.example.test/slow_download/", StringComparison.Ordinal)));
            Assert.IsFalse(sources.Requests.Any(request => request.Contains("fast_download", StringComparison.Ordinal)));
        }

        [TestMethod]
        public async Task AnnasArchive_ChecksWaitForAPermittedPersonAndThenContinue()
        {
            using var storage = new TemporaryDirectory();
            using var downloads = new TemporaryDirectory();
            var sources = new FakeSources { SlowGate = true };
            await using var host = await CreateHostAsync(storage.Path, downloads.Path, sources);
            using var created = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("Anna", AcquisitionConnectionType.AnnasArchive,
                "https://annas.example.test", null, true, 60, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var wantedId = await CreateWantedBookAsync(host);
            await GrabAnnaAsync(host, wantedId);

            // The check is never passed by Jularr; the download waits and the source is not asked again.
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual("source_manual_link", await host.ScalarAsync("SELECT \"AttentionKey\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            var asked = sources.Requests.Count(request => request.StartsWith("annas.example.test/slow_download/", StringComparison.Ordinal));
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.AreEqual(asked, sources.Requests.Count(request => request.StartsWith("annas.example.test/slow_download/", StringComparison.Ordinal)));
            var operationId = (long)(await host.ScalarAsync("SELECT \"Id\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"))!;
            // The optional module hides the wizard and rejects submissions, without losing the waiting job.
            using var modulesResponse = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/instance/modules");
            var assistance = (await modulesResponse.Content.ReadFromJsonAsync<BaseDataList<InstanceModuleDetailsV1>>())!.Single(module => module.Key == "SourceAssistance");
            using (var disabled = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/" + assistance.Id,
                new UpdateInstanceModuleDataV1(false, assistance.Version)))
            {
                Assert.AreEqual(HttpStatusCode.OK, disabled.StatusCode, await disabled.Content.ReadAsStringAsync());
            }

            using (var hidden = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/activity?activityTabTypeId=ToDo"))
            {
                var row = (await hidden.Content.ReadFromJsonAsync<PageResult<ActivityItemV1>>())!.Items.Single(item => item.Id == operationId);
                Assert.IsNull(row.ManualUrl);
            }

            using (var blocked = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/activity/" + operationId + "/source-link",
                new SaveActivitySourceLinkDataV1("https://files.example.test/0123.epub")))
            {
                Assert.AreNotEqual(HttpStatusCode.OK, blocked.StatusCode);
            }

            using (var enabled = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/modules/" + assistance.Id,
                new UpdateInstanceModuleDataV1(true, assistance.Version + 1)))
            {
                Assert.AreEqual(HttpStatusCode.OK, enabled.StatusCode, await enabled.Content.ReadAsStringAsync());
            }

            using var todo = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/activity?activityTabTypeId=ToDo");
            Assert.AreEqual("https://annas.example.test/slow_download/0123456789abcdef0123456789abcdef/0/0",
                (await todo.Content.ReadFromJsonAsync<PageResult<ActivityItemV1>>())!.Items.Single(item => item.Id == operationId).ManualUrl);
            using var plain = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/activity/" + operationId + "/source-link", new SaveActivitySourceLinkDataV1("http://files.example.test/0123.epub"));
            Assert.AreEqual(HttpStatusCode.BadRequest, plain.StatusCode);
            using var local = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/activity/" + operationId + "/source-link",
                new SaveActivitySourceLinkDataV1("https://127.0.0.1/private.epub"));
            Assert.AreEqual(HttpStatusCode.BadRequest, local.StatusCode);
            using var internalHost = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/activity/" + operationId + "/source-link",
                new SaveActivitySourceLinkDataV1("https://printer.local/file.epub"));
            Assert.AreEqual(HttpStatusCode.BadRequest, internalHost.StatusCode);

            // Users see and confirm only while the instance and their account allow it.
            using var assigned = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/accounts/41/profiles/202");
            Assert.AreEqual(HttpStatusCode.OK, assigned.StatusCode, await assigned.Content.ReadAsStringAsync());
            using var switched = await host.SendAsync(HttpMethod.Put, "/api/v1/auth/profile/202");
            Assert.AreEqual(HttpStatusCode.OK, switched.StatusCode, await switched.Content.ReadAsStringAsync());
            Assert.IsNull((await ReadOwnRequestAsync(host)).ManualUrl);
            var link = new SaveRequestSourceLinkDataV1(operationId, "https://files.example.test/0123.epub");
            using (var refused = await host.SendAsync(HttpMethod.Post, "/api/v1/requests/710/source-link", link))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, refused.StatusCode);
            }

            using var settings = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/instance/settings");
            var instance = (await settings.Content.ReadFromJsonAsync<InstanceSettingsDetailsV1>())!;
            using var allowed = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/instance/settings", new UpdateInstanceSettingsDataV1(instance.Name, instance.UiLocaleId, instance.Version, usersConfirmSources: true));
            Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode, await allowed.Content.ReadAsStringAsync());
            Assert.IsNull((await ReadOwnRequestAsync(host)).ManualUrl);
            using var account = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/accounts/41");
            var owner = (await account.Content.ReadFromJsonAsync<AccountDetailsV1>())!;
            Assert.IsFalse(owner.MayConfirmSources);
            using var permitted = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/accounts/41", new UpdateAccountDataV1(owner.DisplayName, owner.AccountRoleTypeId, owner.IsEnabled, true));
            Assert.AreEqual(HttpStatusCode.OK, permitted.StatusCode, await permitted.Content.ReadAsStringAsync());
            var request = await ReadOwnRequestAsync(host);
            Assert.AreEqual(operationId, request.OperationId);
            StringAssert.StartsWith(request.ManualUrl, "https://annas.example.test/slow_download/");
            using (var other = await host.SendAsync(HttpMethod.Post, "/api/v1/requests/999/source-link", link))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);
            }

            using var page = await host.SendAsync(HttpMethod.Get, "/Requests");
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            StringAssert.Contains(html, "handler=SourceLink");
            StringAssert.Contains(html, "source-assistance");
            StringAssert.Contains(html, "type=\"url\"");
            using var confirmed = await host.SendAsync(HttpMethod.Post, "/api/v1/requests/710/source-link", link);
            Assert.AreEqual(HttpStatusCode.OK, confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());

            // The pasted file link is fetched without any key; the import follows as usual.
            await RunAsync<MonitorDownloadsV1>(host);
            Assert.IsTrue(File.Exists(Path.Combine(storage.Path, "1", "Books", "Jane Austen", "Pride and Prejudice (1813)", "Pride and Prejudice.epub")));
            Assert.IsNull((await ReadOwnRequestAsync(host)).ManualUrl);
        }

        [TestMethod]
        public async Task AnnasArchive_LongCountdownWaitsForAPerson()
        {
            var sources = new FakeSources { SlowWaitSeconds = 600 };
            var client = new AnnasArchiveClient(new HttpClient(sources), new Integrations.Logic.Common.NoSourceGateResolver());
            using var target = new TemporaryDirectory();
            var failure = await Assert.ThrowsExactlyAsync<Integrations.Data.AnnasArchive.AnnasArchiveException>(() =>
                client.SlowDownloadAsync(new Uri("https://annas.example.test/"), "0123456789abcdef0123456789abcdef", Path.Combine(target.Path, "book.epub"), TimeSpan.FromSeconds(90), CancellationToken.None));
            Assert.AreEqual(Integrations.Data.AnnasArchive.AnnasArchiveFailure.WaitTooLong, failure.Failure);
            Assert.IsFalse(sources.Requests.Any(request => request.StartsWith("files.example.test", StringComparison.Ordinal)));
        }

        [TestMethod]
        public async Task AnnasArchive_FileRedirectToLocalHost_IsRejected()
        {
            var sources = new FakeSources { RedirectFileToLocal = true };
            var client = new AnnasArchiveClient(new HttpClient(sources), new Integrations.Logic.Common.NoSourceGateResolver());
            using var target = new TemporaryDirectory();

            await Assert.ThrowsExactlyAsync<Integrations.Data.AnnasArchive.AnnasArchiveException>(() =>
                client.DownloadFileAsync(new Uri("https://files.example.test/0123.epub"), Path.Combine(target.Path, "book.epub"), CancellationToken.None));
            Assert.IsTrue(sources.Requests.Any(request => request.StartsWith("files.example.test/", StringComparison.Ordinal)));
            Assert.IsFalse(sources.Requests.Any(request => request.StartsWith("127.0.0.1/", StringComparison.Ordinal)));
        }

        [TestMethod]
        public async Task SimulatedGates_PassOnlyForAllowedTestTargets()
        {
            // The override lives in the tests; it knows only its own simulated challenge and allowed hosts.
            var sources = new FakeSources { Challenge = true };
            var resolver = new TestGateResolver(["annas.example.test"], FakeSources.ChallengeToken);
            var client = new AnnasArchiveClient(new HttpClient(sources), resolver);
            Assert.AreEqual(1, (await client.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice Austen", CancellationToken.None)).Count);

            var foreign = new AnnasArchiveClient(new HttpClient(sources), resolver);
            await Assert.ThrowsExactlyAsync<Integrations.Data.AnnasArchive.AnnasArchiveException>(() =>
                foreign.SearchAsync(new Uri("https://other.example.test/"), "Pride and Prejudice Austen", CancellationToken.None));
            var production = new AnnasArchiveClient(new HttpClient(sources), new Integrations.Logic.Common.NoSourceGateResolver());
            await Assert.ThrowsExactlyAsync<Integrations.Data.AnnasArchive.AnnasArchiveException>(() =>
                production.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice Austen", CancellationToken.None));

            // A wrong token is not accepted even for an allowed host.
            var wrong = new AnnasArchiveClient(new HttpClient(sources), new TestGateResolver(["annas.example.test"], "guess"));
            await Assert.ThrowsExactlyAsync<Integrations.Data.AnnasArchive.AnnasArchiveException>(() =>
                wrong.SearchAsync(new Uri("https://annas.example.test/"), "Pride and Prejudice Austen", CancellationToken.None));
            Assert.IsFalse(new DirectCredentials(new Uri("https://opds.example.test/opds"), "Basic x").Covers(new Uri("https://cdn.example.test/file")));
        }

        private static async Task GrabAnnaAsync(Host host, long wantedId)
        {
            using var searched = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/search", new SearchWantedDataV1());
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, await searched.Content.ReadAsStringAsync());
            var anna = (await searched.Content.ReadFromJsonAsync<BaseDataList<WantedReleaseCandidateV1>>())!.Single(candidate => candidate.ConnectionName == "Anna");
            using var grabbed = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/wanted/" + wantedId + "/grab", new GrabWantedReleaseDataV1(anna.CandidateId));
            Assert.AreEqual(HttpStatusCode.OK, grabbed.StatusCode, await grabbed.Content.ReadAsStringAsync());
        }

        private static async Task<RequestDetailsV1> ReadOwnRequestAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/requests");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<PageResult<RequestDetailsV1>>())!.Items.Single(item => item.Id == 710);
        }

        private static async Task<IReadOnlyList<SourceCatalogItemV1>> ReadCatalogAsync(Host host)
        {
            using var response = await host.SendAsync(HttpMethod.Get, "/api/v1/admin/acquisition/sources");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<BaseDataList<SourceCatalogItemV1>>())!;
        }

        private static async Task<Host> CreateHostAsync(string storage, string downloads, FakeSources sources)
        {
            var host = await LibraryTests.CreateHostAsync(storage, razor: true, settings: new Dictionary<string, string> { ["Storage:DirectDownloadPath"] = downloads }, services: services =>
            {
                services.AddHttpClient<OpdsClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
                services.AddHttpClient<AnnasArchiveClient>().ConfigurePrimaryHttpMessageHandler(() => sources);
            });
            var mountId = await LibraryTests.CreateMountAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE");
            Directory.CreateDirectory(Path.Combine(storage, mountId.ToString(System.Globalization.CultureInfo.InvariantCulture), "Books"));
            await LibraryTests.CreateRootAsync(host, mountId, "Books", MediaType.Book);
            using var client = await host.SendAsync(HttpMethod.Post, Connections, new SaveAcquisitionConnectionDataV1("SAB", AcquisitionConnectionType.Sabnzbd, "https://sab.example.test", "client-key",
                true, 10, [], "books", "/downloads", downloads));
            Assert.AreEqual(HttpStatusCode.OK, client.StatusCode, await client.Content.ReadAsStringAsync());
            return host;
        }

        private static async Task<long> CreateWantedBookAsync(Host host)
        {
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(MediaType.Book, "Pride and Prejudice", 1813));
            var work = (await created.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "WorkMetadataFacts" ("WorkId", "FactKey", "Value", "IsManual") VALUES ({work.Id}, 'Author', 'Jane Austen', TRUE);
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "UiLocaleId", "AcquisitionRequestStatusTypeId", "DecidedByAccountId", "DecidedAt")
                VALUES (710, {work.Id}, 202, 1, 2, 41, now());
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (810, {work.Id});
                INSERT INTO "RequestTargets" ("AcquisitionRequestId", "WantedItemId") VALUES (710, 810);
                """);
            return 810;
        }

        private static async Task RunAsync<TService>(Host host) where TService : Service.Core.SystemService<AcquisitionCycleParametersV1, NoData>
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TService>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default);
        }

        // Test-only override: passes the simulated challenge of explicitly allowed test hosts.
        private sealed class TestGateResolver(string[] allowedHosts, string expectedToken) : ISourceGateResolver
        {
            public Task<string?> ResolveAsync(SourceGate gate, CancellationToken ct) => Task.FromResult(
                gate.Uri.Host.EndsWith(".test", StringComparison.Ordinal) && allowedHosts.Contains(gate.Uri.Host) && gate.ChallengeToken == FakeSources.ChallengeToken
                    ? "gate=" + expectedToken
                    : null);
        }

        private sealed class FakeSources : HttpMessageHandler
        {
            public const string User = "reader";
            public const string Password = "opds-secret-password";
            public const string MemberKey = "member-secret-key";
            public const string ChallengeToken = "simulated-challenge";

            public bool Challenge { get; set; }
            public bool SlowGate { get; set; }
            public bool RedirectFileToLocal { get; set; }
            public int SlowWaitSeconds { get; set; } = 1;
            public int SignedHits { get; private set; }
            public ConcurrentBag<string> AuthorizationSeenBy { get; } = [];
            public ConcurrentBag<string> Requests { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                Requests.Add(uri.Host + uri.PathAndQuery);
                if (request.Headers.Authorization is not null)
                {
                    AuthorizationSeenBy.Add(uri.Host);
                }

                return Task.FromResult(uri.Host switch
                {
                    "opds.example.test" => Opds(request),
                    "cdn.example.test" => Signed(uri),
                    "annas.example.test" or "other.example.test" => Annas(request),
                    "files.example.test" when RedirectFileToLocal => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://127.0.0.1/secret") } },
                    "files.example.test" => Bytes(ReadingTests.BuildEpub("Pride and Prejudice")),
                    _ => Text("""{"queue":{"paused":false,"slots":[]},"history":{"slots":[]},"version":"4.5.0"}""", "application/json")
                });
            }

            private static HttpResponseMessage Opds(HttpRequestMessage request)
            {
                var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(User + ":" + Password));
                if (request.Headers.Authorization?.ToString() != expected)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                return request.RequestUri!.AbsolutePath switch
                {
                    "/opds" => Text("""<feed xmlns="http://www.w3.org/2005/Atom"><title>Calibre</title><link rel="search" type="application/opensearchdescription+xml" href="/opds/osd"/></feed>""", "application/atom+xml"),
                    "/opds/osd" => Text("""<OpenSearchDescription xmlns="http://a9.com/-/spec/opensearch/1.1/"><Url type="application/atom+xml;profile=opds-catalog" template="https://opds.example.test/opds/search?q={searchTerms}"/></OpenSearchDescription>""", "application/opensearchdescription+xml"),
                    "/opds/search" => Text("""
                        <feed xmlns="http://www.w3.org/2005/Atom" xmlns:dc="http://purl.org/dc/terms/">
                          <entry><id>urn:calibre:1</id><title>Pride and Prejudice</title><author><name>Jane Austen</name></author><dc:language>en</dc:language>
                            <link rel="http://opds-spec.org/acquisition" type="application/epub+zip" href="/download/1.epub"/>
                          </entry>
                        </feed>
                        """, "application/atom+xml"),
                    "/download/1.epub" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://cdn.example.test/signed/1.epub?sig=" + Guid.NewGuid().ToString("N")) } },
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            }

            // The first signed link has expired by the time it is used.
            private HttpResponseMessage Signed(Uri uri)
            {
                SignedHits++;
                return SignedHits == 1 ? new HttpResponseMessage(HttpStatusCode.Gone) : Bytes(ReadingTests.BuildEpub("Pride and Prejudice"));
            }

            private HttpResponseMessage Annas(HttpRequestMessage request)
            {
                var uri = request.RequestUri!;
                if (Challenge && !(request.Headers.TryGetValues("Cookie", out var cookies) && cookies.Contains("gate=" + ChallengeToken)))
                {
                    var gate = Text("<html><body>Checking your browser</body></html>", "text/html", HttpStatusCode.ServiceUnavailable);
                    gate.Headers.Add("cf-mitigated", "challenge");
                    gate.Headers.Add("X-Challenge-Token", ChallengeToken);
                    return gate;
                }

                // The simulated slow download shows a check or a countdown with the file link.
                if (uri.AbsolutePath.StartsWith("/slow_download/0123456789abcdef0123456789abcdef/0/", StringComparison.Ordinal))
                {
                    if (!SlowGate)
                    {
                        return Text($"""<html><body><p>Please wait {SlowWaitSeconds} seconds before downloading.</p><a href="/md5/0123456789abcdef0123456789abcdef">Back</a><a href="https://files.example.test/0123.epub">Download now</a></body></html>""", "text/html");
                    }

                    var check = Text("<html><body>Checking your browser</body></html>", "text/html", HttpStatusCode.ServiceUnavailable);
                    check.Headers.Add("cf-mitigated", "challenge");
                    return check;
                }

                return uri.AbsolutePath switch
                {
                    "/search" => Text("""
                        <html><body>
                          <a href="/md5/0123456789abcdef0123456789abcdef"><h3>Pride and Prejudice</h3><div class="italic">Jane Austen</div><div>English [en], .epub, 1.2MB, Pride and Prejudice.epub</div></a>
                          <a href="/md5/0123456789abcdef0123456789abcdef">duplicate</a>
                          <a href="/md5/not-a-hash"><h3>Broken</h3><div>.epub</div></a>
                        </body></html>
                        """, "text/html"),
                    "/dyn/api/fast_download.json" when uri.Query.Contains("key=" + MemberKey, StringComparison.Ordinal)
                        => Text("""{"download_url":"https://files.example.test/0123.epub"}""", "application/json"),
                    "/dyn/api/fast_download.json" => Text("""{"error":"Invalid key"}""", "application/json", HttpStatusCode.Forbidden),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            }

            private static HttpResponseMessage Text(string text, string mediaType, HttpStatusCode status = HttpStatusCode.OK) =>
                new(status) { Content = new StringContent(text, Encoding.UTF8, mediaType) };

            private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}
