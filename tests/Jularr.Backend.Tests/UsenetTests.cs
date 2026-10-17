using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.System.Acquisition.V1;
using Jularr.Integrations.Data.Usenet;
using Jularr.Integrations.Logic.Newznab;
using Jularr.Integrations.Logic.Usenet;
using Jularr.Integrations.Logic.Storage;
using Microsoft.Extensions.DependencyInjection;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class UsenetTests
    {
        private const string Connections = "/api/v1/admin/acquisition/connections";
        private const string Feature = "Example.Movie.2020.1080p.WEB-DL.H264-GRP";

        [TestMethod]
        public void NativeDownload_RequiresConfirmedWorkspaceAndNeverUsesImplicitTemp()
        {
            using var directory = new TemporaryDirectory();
            var options = new StorageOptions();
            Assert.IsNull(options.ReadyUsenetDownloadPath());
            Assert.Throws<ServiceUnavailableException>(() => WantedAcquisition.Staging(options, 42));

            options.DirectDownloadPath = "relative/downloads";
            Assert.IsNull(options.ReadyUsenetDownloadPath());
            options.DirectDownloadPath = Path.Combine(directory.Path, "unmounted");
            Assert.IsNull(options.ReadyUsenetDownloadPath());

            options.DirectDownloadPath = directory.Path;
            if (OperatingSystem.IsLinux() && !StorageFileSystem.IsMountPoint(directory.Path))
            {
                Assert.IsNull(options.ReadyUsenetDownloadPath(), "The host's root filesystem must not count as a confirmed workspace.");
            }

            options.AllowDirectDownloadOnHostFilesystem = true;
            options.DirectDownloadPath = Path.GetPathRoot(directory.Path);
            Assert.IsNull(options.ReadyUsenetDownloadPath(), "A filesystem root is not a staging workspace.");
            options.DirectDownloadPath = directory.Path;
            Assert.AreEqual(Path.GetFullPath(directory.Path), options.ReadyUsenetDownloadPath());
            Assert.AreEqual(Path.Combine(Path.GetFullPath(directory.Path), "42"), WantedAcquisition.Staging(options, 42));
            Assert.AreEqual(Path.GetFullPath(directory.Path), options.ReadyDirectSourceDownloadPath(), "An explicitly set shared workspace remains compatible.");

            using var books = new TemporaryDirectory();
            options.DirectSourceDownloadPath = books.Path;
            Assert.AreEqual(Path.GetFullPath(books.Path), options.ReadyDirectSourceDownloadPath());
            Assert.AreEqual(Path.Combine(Path.GetFullPath(books.Path), "43"), WantedAcquisition.DirectStaging(options, 43));
            Assert.AreEqual(Path.GetFullPath(directory.Path), options.ReadyUsenetDownloadPath(), "Book download configuration must not change NNTP staging.");
        }

        [TestMethod]
        public async Task Usenet_OwnDownloaderFetchesTheNzbAndArticlesAndImports()
        {
            using var storage = new TemporaryDirectory();
            using var staging = new TemporaryDirectory();
            var movie = Enumerable.Range(0, 300_000).Select(index => (byte)(index * 7 % 256)).ToArray();
            await using var news = new FakeNewsServer("reader", "news-secret");
            news.Publish(Feature + ".mkv", movie, 3);
            var indexer = new FakeIndexer(news.Nzb(Feature + ".mkv"));
            await using var host = await CreateHostAsync(storage.Path, staging.Path, indexer, news.Port);

            // A login and one command prove the news server; the password is never shown again.
            using var tested = await host.SendAsync(HttpMethod.Post, Connections + "/2/test");
            Assert.IsTrue((await tested.Content.ReadFromJsonAsync<AcquisitionConnectionTestV1>())!.IsAvailable);
            using var listed = await host.SendAsync(HttpMethod.Get, Connections);
            Assert.IsFalse((await listed.Content.ReadAsStringAsync()).Contains("news-secret", StringComparison.Ordinal));

            // The automatic search hands the release to Jularr itself; the NZB waits in staging.
            await CreateWantedMovieAsync(host);
            await RunAsync<SearchDueWantedV1>(host);
            Assert.AreEqual("usenet", await host.ScalarAsync("SELECT \"ExternalId\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            Assert.AreEqual(1, indexer.NzbRequests);

            // Passes download the articles, the import places the feature like any client's.
            for (var pass = 0; pass < 5 && await RunAsync<DownloadUsenetV1>(host); pass++)
            {
            }

            var feature = Path.Combine(storage.Path, "1", "Movies", "Example Movie (2020)", "Example Movie (2020) [WEBDL-1080p].mkv");
            Assert.IsTrue(File.Exists(feature));
            CollectionAssert.AreEqual(movie, File.ReadAllBytes(feature));
            Assert.AreEqual((short)3, await host.ScalarAsync("SELECT \"OperationStatusTypeId\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(staging.Path).Length);
            Assert.IsTrue(news.Logins.All(login => login == "reader:news-secret"));
        }

        [TestMethod]
        public async Task Usenet_MissingArticlesWithoutRepairFiles_FailTheRelease()
        {
            using var storage = new TemporaryDirectory();
            using var staging = new TemporaryDirectory();
            await using var news = new FakeNewsServer("reader", "news-secret");
            news.Publish(Feature + ".mkv", new byte[100_000], 2);
            news.Remove(Feature + ".mkv", 2);
            await using var host = await CreateHostAsync(storage.Path, staging.Path, new FakeIndexer(news.Nzb(Feature + ".mkv")), news.Port);
            await CreateWantedMovieAsync(host);
            await RunAsync<SearchDueWantedV1>(host);
            await RunAsync<DownloadUsenetV1>(host);
            Assert.AreEqual("download_failed", await host.ScalarAsync("SELECT \"ReasonKey\" FROM \"AcquisitionReleases\""));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(staging.Path).Length);
        }

        [TestMethod]
        public async Task Usenet_PackedReleaseWithoutExtractor_WaitsWithoutRetryingAtOnce()
        {
            using var storage = new TemporaryDirectory();
            using var staging = new TemporaryDirectory();
            await using var news = new FakeNewsServer("reader", "news-secret");
            news.Publish(Feature + ".rar", new byte[50_000], 2);
            await using var host = await CreateHostAsync(storage.Path, staging.Path, new FakeIndexer(news.Nzb(Feature + ".rar")), news.Port);
            await CreateWantedMovieAsync(host);
            await RunAsync<SearchDueWantedV1>(host);

            // The download finishes, the unpacking waits for the configured program and the worker rests.
            await RunAsync<DownloadUsenetV1>(host);
            Assert.AreEqual("usenet_extractor_missing", await host.ScalarAsync("SELECT \"AttentionKey\" FROM \"Operations\" WHERE \"OperationTypeId\" = 2"));
            var logins = news.Logins.Count;
            Assert.IsFalse(await RunAsync<DownloadUsenetV1>(host));
            Assert.AreEqual(logins, news.Logins.Count);
        }

        [TestMethod]
        public void Usenet_YencAndNzb_DecodeCheckAndRejectInjection()
        {
            // yEnc parts land at their offset and carry a checksum.
            var data = Enumerable.Range(0, 1000).Select(index => (byte)index).ToArray();
            var part = YencDecoder.Decode(Encoding.Latin1.GetBytes(FakeNewsServer.Encode("a.bin", data, 501, 1000, 2000)));
            Assert.AreEqual(500L, part.Offset);
            Assert.IsTrue(part.IsIntact);
            CollectionAssert.AreEqual(data[500..], part.Data);
            var broken = System.Text.RegularExpressions.Regex.Replace(FakeNewsServer.Encode("a.bin", data, 501, 1000, 2000), "pcrc32=[0-9a-f]+", "pcrc32=00000000");
            Assert.IsFalse(YencDecoder.Decode(Encoding.Latin1.GetBytes(broken)).IsIntact);

            // Message ids with line breaks or brackets never reach the server; names never leave the folder.
            var nzb = """
                <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
                  <file subject="&quot;../../evil.mkv&quot; yEnc (1/2)"><segments>
                    <segment bytes="10" number="1">good@example.test</segment>
                    <segment bytes="10" number="2">bad@example.test&#13;&#10;QUIT</segment>
                    <segment bytes="10" number="3">&lt;x&gt;y@example.test</segment>
                  </segments></file>
                </nzb>
                """;
            var file = NzbReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(nzb))).Single();
            Assert.AreEqual("evil.mkv", file.FileName);
            CollectionAssert.AreEqual(new[] { "good@example.test" }, file.Segments.Select(segment => segment.MessageId).ToArray());
        }

        private static async Task<Host> CreateHostAsync(string storage, string staging, FakeIndexer indexer, int newsPort)
        {
            var host = await LibraryTests.CreateHostAsync(storage, settings: new Dictionary<string, string>
                {
                    ["Storage:DirectDownloadPath"] = staging,
                    ["Storage:AllowDirectDownloadOnHostFilesystem"] = "true"
                },
                services: services => services.AddHttpClient<NewznabClient>().ConfigurePrimaryHttpMessageHandler(() => indexer));
            var mountId = await LibraryTests.CreateMountAsync(host);
            await host.Fixture.RunSqlAsync("UPDATE \"StorageMounts\" SET \"IsReadOnly\" = FALSE");
            Directory.CreateDirectory(Path.Combine(storage, mountId.ToString(CultureInfo.InvariantCulture), "Movies"));
            await LibraryTests.CreateRootAsync(host, mountId, "Movies", MediaType.Movie);

            // A news server is a host with SSL; a web address belongs to the other connections.
            using var web = await host.SendAsync(HttpMethod.Post, Connections,
                new SaveAcquisitionConnectionDataV1("News", AcquisitionConnectionType.Usenet, "https://news.example.test", null, true, 10, [], null, null, null, "reader", 2, host: "news.example.test",
                    useSsl: true));
            Assert.AreEqual(HttpStatusCode.BadRequest, web.StatusCode, await web.Content.ReadAsStringAsync());
            using var hostless = await host.SendAsync(HttpMethod.Post, Connections,
                new SaveAcquisitionConnectionDataV1("Indexer", AcquisitionConnectionType.Newznab, "https://indexer.example.test/", null, true, 10, [], null, null, null, host: "news.example.test"));
            Assert.AreEqual(HttpStatusCode.BadRequest, hostless.StatusCode, await hostless.Content.ReadAsStringAsync());
            foreach (var data in new[]
            {
                new SaveAcquisitionConnectionDataV1("Indexer", AcquisitionConnectionType.Newznab, "https://indexer.example.test/", "indexer-key", true, 10, [], null, null, null),
                new SaveAcquisitionConnectionDataV1("News", AcquisitionConnectionType.Usenet, null, "news-secret", true, 10, [], null, null, null, "reader", 2, host: "127.0.0.1",
                    port: newsPort, useSsl: false)
            })
            {
                using var created = await host.SendAsync(HttpMethod.Post, Connections, data);
                Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            }

            return host;
        }

        private static async Task CreateWantedMovieAsync(Host host)
        {
            using var created = await host.SendAsync(HttpMethod.Post, "/api/v1/admin/works", new CreateWorkDataV1(MediaType.Movie, "Example Movie", 2020));
            var work = (await created.Content.ReadFromJsonAsync<WorkDetailsV1>())!;
            await host.Fixture.RunSqlAsync($"""
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId", "AcquisitionRequestStatusTypeId", "DecidedByAccountId", "DecidedAt")
                VALUES (500, {work.Id}, 202, 42, 1, 2, 41, now());
                INSERT INTO "WantedItems" ("Id", "WorkId") VALUES (600, {work.Id});
                INSERT INTO "RequestTargets" ("AcquisitionRequestId", "WantedItemId") VALUES (500, 600);
                """);
        }

        private static async Task<bool> RunAsync<TService>(Host host) where TService : Service.Core.SystemService<AcquisitionCycleParametersV1, NoData>
        {
            await using var scope = host.Services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<TService>().ExecuteAsync<UpdateResult>(new AcquisitionCycleParametersV1(), default)).Success;
        }

        private sealed class FakeIndexer(string nzb) : HttpMessageHandler
        {
            public int NzbRequests { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath == "/getnzb/feature")
                {
                    NzbRequests++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(nzb, Encoding.UTF8, "application/x-nzb") });
                }

                var body = uri.Query.Contains("t=caps", StringComparison.Ordinal)
                    ? """<caps><limits max="100" default="100" /><searching><search available="yes" supportedParams="q" /><movie-search available="yes" supportedParams="q" /></searching></caps>"""
                    : $"""<rss><channel><item><title>{Feature}</title><guid>feature</guid><link>https://indexer.example.test/getnzb/feature</link><size>2000000000</size></item></channel></rss>""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/rss+xml") });
            }
        }

        // A loopback news server speaking just enough NNTP: login, DATE, BODY with dot-stuffing, QUIT.
        private sealed class FakeNewsServer : IAsyncDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new();
            private readonly Dictionary<string, string> _articles = new(StringComparer.Ordinal);
            private readonly Dictionary<string, List<(string Id, int Bytes)>> _files = new(StringComparer.Ordinal);
            private readonly string _user;
            private readonly string _password;
            private readonly Task _accepting;

            public FakeNewsServer(string user, string password)
            {
                _user = user;
                _password = password;
                _listener.Start();
                _accepting = AcceptAsync();
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public List<string> Logins { get; } = [];

            public void Publish(string name, byte[] data, int parts)
            {
                var size = (data.Length + parts - 1) / parts;
                var segments = new List<(string, int)>();
                for (var part = 0; part < parts; part++)
                {
                    var begin = part * size;
                    var end = Math.Min(data.Length, begin + size);
                    var id = $"{name}.{part + 1}@news.example.test";
                    _articles[id] = Encode(name, data, begin + 1, end, data.Length);
                    segments.Add((id, end - begin));
                }

                _files[name] = segments;
            }

            public void Remove(string name, int part) => _articles.Remove(_files[name][part - 1].Id);

            public string Nzb(string name) => $"""
                <?xml version="1.0" encoding="utf-8"?>
                <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
                  <file poster="tester" date="1700000000" subject="Example [1/1] - &quot;{name}&quot; yEnc (1/{_files[name].Count})">
                    <groups><group>alt.binaries.test</group></groups>
                    <segments>{string.Concat(_files[name].Select((segment, index) => $"""<segment bytes="{segment.Bytes}" number="{index + 1}">{segment.Id}</segment>"""))}</segments>
                  </file>
                </nzb>
                """;

            public static string Encode(string name, byte[] data, int begin, int end, int size)
            {
                var slice = data[(begin - 1)..end];
                var text = new StringBuilder();
                text.Append($"=ybegin part=1 line=128 size={size} name={name}\r\n=ypart begin={begin} end={end}\r\n");
                var column = 0;
                foreach (var value in slice)
                {
                    var encoded = (byte)(value + 42);
                    if (encoded is 0 or 10 or 13 or 61)
                    {
                        text.Append('=');
                        encoded = (byte)(encoded + 64);
                        column++;
                    }

                    text.Append((char)encoded);
                    if (++column >= 128)
                    {
                        text.Append("\r\n");
                        column = 0;
                    }
                }

                var crc = YencDecoder.Checksum(slice).ToString("x8", CultureInfo.InvariantCulture);
                return text.Append($"\r\n=yend size={slice.Length} part=1 pcrc32={crc}\r\n").ToString();
            }

            private async Task AcceptAsync()
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    _ = ServeAsync(client);
                }
            }

            private async Task ServeAsync(TcpClient client)
            {
                using (client)
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.Latin1);
                    await Write(stream, "200 fake news ready\r\n");
                    string? user = null;
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        var parts = line.Split(' ', 3);
                        switch (parts[0].ToUpperInvariant())
                        {
                            case "AUTHINFO" when parts[1] == "USER":
                                user = parts[2];
                                await Write(stream, "381 password\r\n");
                                break;
                            case "AUTHINFO":
                                lock (Logins) Logins.Add(user + ":" + parts[2]);
                                await Write(stream, user == _user && parts[2] == _password ? "281 welcome\r\n" : "481 no\r\n");
                                break;
                            case "DATE":
                                await Write(stream, "111 20261013120000\r\n");
                                break;
                            case "BODY" when _articles.TryGetValue(parts[1].Trim('<', '>'), out var article):
                                var stuffed = string.Join("\r\n", article.Split("\r\n").Select(text => text.StartsWith('.') ? "." + text : text));
                                await Write(stream, "222 0 body\r\n" + stuffed + (stuffed.EndsWith("\r\n", StringComparison.Ordinal) ? "" : "\r\n") + ".\r\n");
                                break;
                            case "BODY":
                                await Write(stream, "430 no such article\r\n");
                                break;
                            case "QUIT":
                                await Write(stream, "205 bye\r\n");
                                return;
                            default:
                                await Write(stream, "500 what\r\n");
                                break;
                        }
                    }
                }
            }

            private static Task Write(NetworkStream stream, string text) => stream.WriteAsync(Encoding.Latin1.GetBytes(text)).AsTask();

            public async ValueTask DisposeAsync()
            {
                await _stop.CancelAsync();
                _listener.Stop();
                await _accepting;
                _stop.Dispose();
            }
        }
    }
}
