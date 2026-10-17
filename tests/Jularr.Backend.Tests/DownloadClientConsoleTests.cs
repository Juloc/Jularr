using System.Net;
using System.Text;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Logic.Sabnzbd;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class DownloadClientConsoleTests
    {
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
                var snapshot = new SabnzbdConsoleSnapshot([], [], null, null, false);
                var result = DownloadClientFolderInspector.Inspect(complete, snapshot);

                Assert.IsTrue(result.CompleteAvailable);
                Assert.IsTrue(result.IncompleteAvailable);
                Assert.AreEqual("unassigned", result.Entries.Single().Assignment);
                Assert.IsTrue(File.Exists(leftovers));
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
                        ? "{\"status\":{\"folders\":[\"stale-folder\"]}}"
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
