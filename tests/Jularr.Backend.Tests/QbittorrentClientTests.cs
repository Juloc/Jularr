using System.Net;
using System.Text;
using System.Text.Json;
using Jularr.Integrations.Data.Qbittorrent;
using Jularr.Integrations.Logic.Qbittorrent;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class QbittorrentClientTests
    {
        [TestMethod]
        public async Task Client_LogsIn_AddsTheTorrent_AndReadsItsState()
        {
            var client = new FakeQbittorrent();
            using var http = new HttpClient(client);
            var sut = new QbittorrentClient(http);
            var connection = new QbittorrentConnection("http://torrent:8080", "admin", "secret");

            // The version alone proves the client answers; the login proves the account.
            var test = await sut.TestAsync(connection, default);
            Assert.IsTrue(test.IsAvailable);
            Assert.AreEqual("v5.3.0", test.Version);
            Assert.AreEqual(1, client.Logins);
            Assert.AreEqual("secret", client.Password);

            // A grab hands the file over and names the torrent the client took.
            var grab = await sut.GrabAsync(connection, new QbittorrentGrabRequest(new Uri("https://nyaa.media/download/1.torrent"), "/downloads/complete", "Example Novel v01"), default);
            Assert.IsTrue(grab.Success, grab.ReasonKey);
            Assert.AreEqual("abc123", grab.Hash);
            Assert.AreEqual("https://nyaa.media/download/1.torrent", client.AddedUrl);
            Assert.AreEqual("/downloads/complete", client.AddedSavePath);

            // The state says how far the torrent is and where its file lies.
            var snapshot = await sut.GetSnapshotAsync(connection, null, default);
            var torrent = snapshot.Torrents.Single();
            Assert.AreEqual("Example Novel v01", torrent.Name);
            Assert.AreEqual("downloading", torrent.State);
            Assert.AreEqual(0.25, torrent.Progress);
            Assert.AreEqual(1024, torrent.SizeBytes);
            Assert.AreEqual(256, torrent.DownloadedBytes);
            Assert.AreEqual(4096, torrent.DownloadSpeed);
            Assert.AreEqual(12, torrent.Seeds);
            Assert.AreEqual(3, torrent.Peers);
            Assert.AreEqual("/downloads/complete/Example Novel v01", torrent.ContentPath);
            Assert.IsTrue(torrent.IsActive);
            Assert.IsFalse(torrent.IsCompleted);

            // Only the wanted torrents are asked for.
            var one = await sut.GetSnapshotAsync(connection, ["abc123"], default);
            Assert.AreEqual("abc123", one.Torrents.Single().Hash);
            Assert.AreEqual("hashes=abc123", client.LastHashFilter);
        }

        [TestMethod]
        public async Task Client_TellsTheFolder_ItWritesInto()
        {
            var client = new FakeQbittorrent();
            using var http = new HttpClient(client);
            var sut = new QbittorrentClient(http);
            var connection = new QbittorrentConnection("http://torrent:8080", "admin", "secret");

            // The folder is what Jularr maps to the storage it reads from.
            var path = await sut.GetDefaultSavePathAsync(connection, default);
            Assert.AreEqual("/downloads/complete", path);
        }

        [TestMethod]
        public async Task Client_RefusesAWrongAccount_AndReportsIt()
        {
            var client = new FakeQbittorrent { AcceptLogin = false };
            using var http = new HttpClient(client);
            var sut = new QbittorrentClient(http);
            var connection = new QbittorrentConnection("http://torrent:8080", "admin", "wrong");

            var test = await sut.TestAsync(connection, default);
            Assert.IsFalse(test.IsAvailable);
            Assert.IsNull(test.Version);

            var grab = await sut.GrabAsync(connection, new QbittorrentGrabRequest(new Uri("https://nyaa.media/download/1.torrent"), "/downloads/complete", "Example"), default);
            Assert.IsFalse(grab.Success);
            Assert.AreEqual("acquisition_unavailable", grab.ReasonKey);
        }

        [TestMethod]
        public async Task Client_Pauses_Resumes_And_RemovesATorrent()
        {
            var client = new FakeQbittorrent();
            using var http = new HttpClient(client);
            var sut = new QbittorrentClient(http);
            var connection = new QbittorrentConnection("http://torrent:8080", "admin", "secret");

            await sut.SetPausedAsync(connection, "abc123", true, default);
            Assert.AreEqual("/api/v2/torrents/stop", client.LastAction);
            await sut.SetPausedAsync(connection, "abc123", false, default);
            Assert.AreEqual("/api/v2/torrents/start", client.LastAction);
            await sut.RemoveAsync(connection, "abc123", true, default);
            Assert.AreEqual("/api/v2/torrents/delete", client.LastAction);
            Assert.AreEqual("abc123", client.LastHashes);
        }

        [TestMethod]
        public async Task Grab_AssignsOnlyTheTaggedTorrent_NotTheNewestOrSameNamedUnrelatedTorrent()
        {
            var client = new FakeQbittorrent { IncludeUnrelated = true };
            using var http = new HttpClient(client);
            var sut = new QbittorrentClient(http);
            var result = await sut.GrabAsync(new QbittorrentConnection("http://torrent:8080", "admin", "secret"),
                new QbittorrentGrabRequest(new Uri("https://nyaa.media/download/1.torrent"), "/downloads/complete", "Example Novel v01"), default);
            Assert.IsTrue(result.Success);
            Assert.AreEqual("abc123", result.Hash);
        }

        [TestMethod]
        public async Task Client_ListsIndividualFilesForActivity()
        {
            var fake = new FakeQbittorrent();
            using var http = new HttpClient(fake);
            var client = new QbittorrentClient(http);
            var files = await client.GetFilesAsync(new QbittorrentConnection("http://torrent:8080", "admin", "secret"), "abc123", default);
            Assert.AreEqual(2, files.Count);
            Assert.AreEqual("novel/volume1.epub", files[0].Name);
            Assert.AreEqual(1024, files[0].SizeBytes);
            Assert.AreEqual(0.6, files[0].Progress);
            Assert.IsTrue(files[0].IsSelected);
            Assert.IsFalse(files[1].IsSelected);
        }

        [TestMethod]
        public void StoppedUploading_IsCompletedAndPaused_AndStoppedDownloadingIsNotComplete()
        {
            var seed = new QbittorrentTorrent("abc", "novel", "stoppedUP", 1, 100, 100, 0, 2, 0, "/downloads/novel", "/downloads");
            var stopped = new QbittorrentTorrent("def", "novel", "stoppedDL", 0.3, 100, 30, 0, 0, 0, "/downloads/novel", "/downloads");
            Assert.IsTrue(seed.IsPaused);
            Assert.IsTrue(seed.IsCompleted);
            Assert.IsTrue(stopped.IsPaused);
            Assert.IsFalse(stopped.IsCompleted);
        }

        // The client as it answers: a session cookie on the login, the version, the list and the actions.
        private sealed class FakeQbittorrent : HttpMessageHandler
        {
            public bool AcceptLogin { get; init; } = true;
            public bool IncludeUnrelated { get; init; } = false;
            public int Logins { get; private set; }
            public string? Password { get; private set; }
            public string? AddedUrl { get; private set; }
            public string? AddedSavePath { get; private set; }
            public string? LastAction { get; private set; }
            public string? LastHashes { get; private set; }
            public string? LastHashFilter { get; private set; }
            private string? Tag { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                var query = request.RequestUri.Query;

                if (path == "/api/v2/auth/login")
                {
                    Logins++;
                    var form = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                    Password = form.Contains("secret", StringComparison.Ordinal) ? "secret" : null;
                    if (!AcceptLogin) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
                    response.Headers.Add("Set-Cookie", "SID=session; path=/; HttpOnly");
                    return Task.FromResult(response);
                }

                // Every other call needs the session the login handed out.
                if (request.Headers.Contains("Cookie") is false)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                }

                switch (path)
                {
                    case "/api/v2/app/version":
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("v5.3.0") });
                    case "/api/v2/app/preferences":
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"save_path\":\"/downloads/complete\"}") });
                    case "/api/v2/torrents/createTags":
                    {
                        var form = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                        Tag = Value(form, "tags");
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                    }
                    case "/api/v2/torrents/add":
                    {
                        var form = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                        AddedUrl = Value(form, "urls");
                        AddedSavePath = Value(form, "savepath");
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") });
                    }
                    case "/api/v2/torrents/info":
                    {
                        if (query.Length > 1) LastHashFilter = query[1..];
                        var json = TorrentJson.Replace("\"added_on\":", "\"tags\":\"" + Tag + "\",\"added_on\":", StringComparison.Ordinal);
                        if (IncludeUnrelated)
                            json = json.Replace("[", "[{\"hash\":\"unrelated\",\"name\":\"Example Novel v01\",\"state\":\"downloading\",\"progress\":0.1,\"size\":100,\"completed\":10,\"dlspeed\":10,\"num_complete\":1,\"num_incomplete\":2,\"added_on\":9999999999},", StringComparison.Ordinal);
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
                    }
                    case "/api/v2/torrents/files":
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                            "[{\"name\":\"novel/volume1.epub\",\"size\":1024,\"progress\":0.6,\"priority\":1},{\"name\":\"note.txt\",\"size\":20,\"progress\":0,\"priority\":0}]",
                            Encoding.UTF8, "application/json") });
                    case "/api/v2/torrents/stop":
                    case "/api/v2/torrents/start":
                    case "/api/v2/torrents/delete":
                    {
                        LastAction = path;
                        var form = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                        LastHashes = Value(form, "hashes");
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") });
                    }
                    default:
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }
            }

            private static string? Value(string form, string name)
            {
                foreach (var pair in form.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = pair.Split('=', 2);
                    if (parts.Length == 2 && parts[0] == name) return Uri.UnescapeDataString(parts[1]);
                }
                return null;
            }

            private const string TorrentJson =
                """
                [
                 {
                  "hash": "abc123",
                  "name": "Example Novel v01",
                  "state": "downloading",
                  "progress": 0.25,
                  "size": 1024,
                  "completed": 256,
                  "dlspeed": 4096,
                  "num_complete": 12,
                  "num_incomplete": 3,
                  "content_path": "/downloads/complete/Example Novel v01",
                  "save_path": "/downloads/complete",
                  "added_on": 1700000000
                 }
                ]
                """;
        }
    }
}
