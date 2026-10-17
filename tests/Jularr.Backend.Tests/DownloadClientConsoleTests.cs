using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Logic.Sabnzbd;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DownloadClientConsoleTests
    {
        [TestMethod]
        [DataRow("cancel", "SABnzbd_nzo_123")]
        [DataRow("removeHistory", "HIST_1")]
        [DataRow("removeOrphan", "stale-folder")]
        public async Task Console_DestructiveApiCommands_RequireConfirmationBeforeProviderCalls(string action, string jobId)
        {
            var handler = new ProviderHandler();
            await using var host = await Host.CreateAsync(media: true,
                services: services => services.AddHttpClient<SabnzbdClient>().ConfigurePrimaryHttpMessageHandler(() => handler));
            using var authentication = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            const string connections = "/api/v1/admin/acquisition/connections";
            using var created = await host.SendAsync(HttpMethod.Post, connections,
                new SaveAcquisitionConnectionDataV1("Console SAB", AcquisitionConnectionType.Sabnzbd, "http://localhost:8080", "secret", true, 25, [], null, null, null));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var id = (await created.Content.ReadFromJsonAsync<CreateAcquisitionConnectionResultV1>())!.Id;
            using var unconfirmed = await host.SendAsync(HttpMethod.Post, connections + "/" + id + "/jobs", new DownloadClientJobCommandV1(jobId, action));
            Assert.AreEqual(HttpStatusCode.BadRequest, unconfirmed.StatusCode, await unconfirmed.Content.ReadAsStringAsync());
            Assert.IsNull(handler.LastRequest);

            using var confirmed = await host.SendAsync(HttpMethod.Post, connections + "/" + id + "/jobs", new DownloadClientJobCommandV1(jobId, action, true));
            Assert.AreEqual(HttpStatusCode.OK, confirmed.StatusCode, await confirmed.Content.ReadAsStringAsync());
            StringAssert.Contains(handler.LastRequest!, action == "removeOrphan" ? "name=delete_orphan" : "name=delete");
            if (action == "cancel")
            {
                StringAssert.Contains(handler.LastRequest!, "del_files=1");
            }
        }

        [TestMethod]
        public void CategoryRouting_KindOverrideOrDefault_IsDeterministic()
        {
            const string map = """{"Movie":"movies","Tv":"series","Anime":"anime","Manga":"comics"}""";
            Assert.AreEqual("movies", AcquisitionConnectionLogic.ResolveDownloadCategory(map, "fallback", AcquisitionKind.Movie));
            Assert.AreEqual("series", AcquisitionConnectionLogic.ResolveDownloadCategory(map, "fallback", AcquisitionKind.Tv));
            Assert.AreEqual("anime", AcquisitionConnectionLogic.ResolveDownloadCategory(map, "fallback", AcquisitionKind.Anime));
            Assert.AreEqual("comics", AcquisitionConnectionLogic.ResolveDownloadCategory(map, "fallback", AcquisitionKind.Manga));
            Assert.AreEqual("fallback", AcquisitionConnectionLogic.ResolveDownloadCategory(map, "fallback", AcquisitionKind.Book));
            Assert.IsNull(AcquisitionConnectionLogic.ResolveDownloadCategory("{}", null, AcquisitionKind.Music));
        }

        [TestMethod]
        public async Task Console_QueueAndHistory_AreRealProviderSnapshots()
        {
            using var client = new HttpClient(new ProviderHandler());
            var provider = new SabnzbdClient(client);
            var connection = new SabnzbdConnection("http://localhost:8080", "secret");
            var snapshot = await provider.ReadConsoleAsync(connection, CancellationToken.None);

            Assert.AreEqual(1, snapshot.Queue.Count);
            Assert.AreEqual("SABnzbd_nzo_123", snapshot.Queue[0].NzoId);
            Assert.AreEqual(50d, snapshot.Queue[0].Percentage);
            Assert.AreEqual(1, snapshot.History.Count);
            Assert.AreEqual(2_048L, snapshot.SpeedBytesPerSecond);
            Assert.AreEqual(107_374_182_400L, snapshot.FreeBytes);
            Assert.AreEqual(1_024L, snapshot.TodayBytes);
            CollectionAssert.AreEqual(new[] { "stale-folder" }, snapshot.OrphanFolders.ToArray());
            Assert.AreEqual("/data/complete", snapshot.ReportedCompletePath);
            Assert.AreEqual("/data/incomplete", snapshot.ReportedIncompletePath);
            CollectionAssert.AreEqual(new[] { "movies", "tv" }, (await provider.ReadCategoriesAsync(connection, CancellationToken.None)).ToArray());
        }

        [TestMethod]
        public async Task Console_JobCommands_SendOnlyExplicitOperations()
        {
            var handler = new ProviderHandler();
            using var client = new HttpClient(handler);
            var provider = new SabnzbdClient(client);
            var connection = new SabnzbdConnection("http://localhost:8080", "secret");

            await provider.ControlJobAsync(connection, "SABnzbd_nzo_123", SabnzbdJobCommand.RemoveFromQueue, CancellationToken.None);
            StringAssert.Contains(handler.LastRequest!, "name=delete");
            StringAssert.Contains(handler.LastRequest!, "del_files=1");
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ControlJobAsync(connection, "../bad", SabnzbdJobCommand.RemoveFromQueue, CancellationToken.None));
            await provider.RemoveOrphanAsync(connection, "stale-folder", CancellationToken.None);
            StringAssert.Contains(handler.LastRequest!, "name=delete_orphan");
        }

        [TestMethod]
        public void Console_UnassignedFiles_AreReportedButNeverRemoved()
        {
            var root = Path.Combine(Path.GetTempPath(), "jularr-console-" + Guid.NewGuid().ToString("N"));
            var complete = Path.Combine(root, "complete");
            var incomplete = Path.Combine(root, "incomplete");
            Directory.CreateDirectory(complete);
            Directory.CreateDirectory(incomplete);
            try
            {
                var leftovers = Path.Combine(complete, "leftover.nfo");
                File.WriteAllText(leftovers, "keep");
                var snapshot = new SabnzbdConsoleSnapshot([], [], null, null, false,
                    reportedCompletePath: "/data/complete", reportedIncompletePath: "/data/incomplete");
                var result = DownloadClientFolderInspector.Inspect(complete, snapshot, incomplete, "/data/complete", "/data/incomplete");

                Assert.IsTrue(result.CompleteAvailable);
                Assert.IsTrue(result.IncompleteAvailable);
                Assert.AreEqual("unassigned", result.Entries.Single().Assignment);
                Assert.IsTrue(File.Exists(leftovers));

                var onlyIncomplete = DownloadClientFolderInspector.Inspect(null, snapshot, incomplete, null, "/data/incomplete");
                Assert.IsFalse(onlyIncomplete.CompleteAvailable);
                Assert.IsTrue(onlyIncomplete.IncompleteAvailable);
                Assert.IsNull(onlyIncomplete.CompletePath);

                var unmatched = DownloadClientFolderInspector.Inspect(complete, snapshot, incomplete, "/unrelated", "/data/incomplete");
                Assert.IsFalse(unmatched.CompleteAvailable);
                Assert.IsTrue(unmatched.IncompleteAvailable);

                var parentMapped = DownloadClientFolderInspector.Inspect(root, snapshot, root, "/data", "/data");
                Assert.IsTrue(parentMapped.CompleteAvailable);
                Assert.IsTrue(parentMapped.IncompleteAvailable);
                Assert.AreEqual(complete, parentMapped.CompletePath);
                Assert.AreEqual(incomplete, parentMapped.IncompletePath);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private sealed class ProviderHandler : HttpMessageHandler
        {
            public string? LastRequest { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.AreEqual(HttpMethod.Post, request.Method);
                Assert.AreEqual("http://localhost:8080/api", request.RequestUri!.ToString());
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                LastRequest = body;
                StringAssert.Contains(body, "apikey=secret");
                var response = body.Contains("mode=server_stats", StringComparison.Ordinal)
                    ? "{\"day\":1024,\"week\":4096,\"month\":8192,\"total\":16384}"
                    : body.Contains("mode=status", StringComparison.Ordinal) && !body.Contains("name=delete_orphan", StringComparison.Ordinal)
                        ? "{\"status\":{\"folders\":[\"stale-folder\"],\"completedir\":\"/data/complete\",\"downloaddir\":\"/data/incomplete\"}}"
                    : body.Contains("mode=get_cats", StringComparison.Ordinal)
                    ? "{\"categories\":[\"movies\",\"tv\"]}"
                    : body.Contains("mode=history", StringComparison.Ordinal) && !body.Contains("name=delete", StringComparison.Ordinal)
                        ? "{\"history\":{\"slots\":[{\"nzo_id\":\"HIST_1\",\"name\":\"Old Show\",\"status\":\"Completed\"}]}}"
                    : body.Contains("mode=queue", StringComparison.Ordinal) && !body.Contains("name=delete", StringComparison.Ordinal)
                        ? "{\"queue\":{\"kbpersec\":\"2\",\"diskspace1\":\"100\",\"slots\":[{\"nzo_id\":\"SABnzbd_nzo_123\",\"filename\":\"Example\",\"percentage\":\"50\"}]}}"
                        : "{\"status\":true}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response, Encoding.UTF8, "application/json")
                };
            }
        }
    }
}
