using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Jularr.Integrations.Data.Usenet;
using Jularr.Integrations.Logic.Usenet;
using TemporaryDirectory = Jularr.Backend.Tests.LibraryTests.TemporaryDirectory;

namespace Jularr.Backend.Tests
{
    // The own downloader against a news server with real round trips, slow logins and a limited speed per connection.
    [TestClass]
    public sealed class UsenetSpeedTests
    {
        private const int ArticleBytes = 700 * 1024;

        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        public async Task Usenet_PipelinedPooledSessions_DownloadEveryArticleIntactAcrossPasses()
        {
            using var staging = new TemporaryDirectory();
            await using var news = new SlowNewsServer(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(30), 2 * 1024 * 1024);
            var file = news.Publish("pass.mkv", 40);
            await using var downloader = new UsenetDownloader();
            var server = new UsenetServer("127.0.0.1", news.Port, false, "reader", "news-secret", 6);
            var (seconds, passes) = await DownloadAllAsync(downloader, server, file, staging.Path, TimeSpan.FromMilliseconds(400));

            CollectionAssert.AreEqual(news.Data(file), await File.ReadAllBytesAsync(Path.Combine(staging.Path, file.FileName)));
            Assert.IsGreaterThan(1, passes, "The test must cover more than one pass.");
            Assert.IsLessThanOrEqualTo(6, news.Logins, "Later passes reuse the open sessions instead of logging in again.");
            Assert.IsGreaterThan(1, news.MostInFlight, "A session keeps more than one article request in flight.");
            TestContext.WriteLine($"{seconds:0.00} s in {passes} passes");
        }

        [TestMethod]
        public async Task Usenet_PooledSessionTheServerClosed_IsReplacedAndTheArticleIsKept()
        {
            using var staging = new TemporaryDirectory();
            await using var news = new SlowNewsServer(TimeSpan.Zero, TimeSpan.Zero, 0);
            var file = news.Publish("drop.mkv", 8);
            await using var downloader = new UsenetDownloader();
            var server = new UsenetServer("127.0.0.1", news.Port, false, "reader", "news-secret", 2);
            var first = await downloader.DownloadAsync(server, [file], staging.Path, new HashSet<string>(file.Segments.Skip(4).Select(segment => segment.MessageId)),
                DateTimeOffset.UtcNow.AddSeconds(10), long.MaxValue, CancellationToken.None);
            news.DropAll();
            var second = await downloader.DownloadAsync(server, [file], staging.Path, new HashSet<string>(first.Done), DateTimeOffset.UtcNow.AddSeconds(10), long.MaxValue,
                CancellationToken.None);

            Assert.AreEqual(4, first.Done.Count);
            Assert.AreEqual(4, second.Done.Count);
            Assert.IsEmpty(second.Damaged);
            CollectionAssert.AreEqual(news.Data(file), await File.ReadAllBytesAsync(Path.Combine(staging.Path, file.FileName)));
        }

        // Opt-in: compares the speed with a slow storage check, 50 connections, 40 ms round trip, 50 ms login and 4 MB/s per connection.
        [TestMethod]
        [TestCategory("Performance")]
        public async Task Usenet_ManyConnectionsWithLatency_ReportsThroughput()
        {
            if (Environment.GetEnvironmentVariable("JULARR_TEST_USENET_BENCHMARK") != "1")
            {
                Assert.Inconclusive("Opt in with JULARR_TEST_USENET_BENCHMARK=1.");
            }

            using var staging = new TemporaryDirectory();
            await using var news = new SlowNewsServer(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(40), 4 * 1024 * 1024);
            var file = news.Publish("bench.mkv", 600);
            await using var downloader = new UsenetDownloader();
            var server = new UsenetServer("127.0.0.1", news.Port, false, "reader", "news-secret", 50);

            // A storage check that costs a NAS round trip, like statvfs and the mount table on a network share.
            var checks = 0;
            IDisposable Reserve(long bytes)
            {
                Interlocked.Increment(ref checks);
                Thread.Sleep(20);
                return new Release();
            }

            var (seconds, passes) = await DownloadAllAsync(downloader, server, file, staging.Path, TimeSpan.FromSeconds(20), Reserve);
            var megabytes = (double)file.Bytes / (1024 * 1024);
            CollectionAssert.AreEqual(news.Data(file), await File.ReadAllBytesAsync(Path.Combine(staging.Path, file.FileName)));
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{megabytes:0} MiB in {seconds:0.0} s = {megabytes / seconds:0.0} MiB/s; {passes} passes, {news.Logins} logins, {checks} storage checks, {news.MostInFlight} in flight"));
        }

        private static async Task<(double Seconds, int Passes)> DownloadAllAsync(UsenetDownloader downloader, UsenetServer server, NzbFile file, string directory, TimeSpan pass,
            Func<long, IDisposable>? reserve = null)
        {
            var finished = new HashSet<string>(StringComparer.Ordinal);
            var watch = Stopwatch.StartNew();
            var passes = 0;
            while (finished.Count < file.Segments.Count)
            {
                passes++;
                var progress = await downloader.DownloadAsync(server, [file], directory, finished, DateTimeOffset.UtcNow + pass, long.MaxValue, CancellationToken.None, 0, reserve);
                Assert.IsEmpty(progress.Damaged);
                finished.UnionWith(progress.Done);
                Assert.IsLessThan(100, passes);
            }

            return (watch.Elapsed.TotalSeconds, passes);
        }

        private sealed class Release : IDisposable
        {
            public void Dispose()
            {
            }
        }

        // Answers every request in order after the round trip; pipelined requests overlap their waits like on a real line.
        private sealed class SlowNewsServer : IAsyncDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new();
            private readonly Dictionary<string, (byte[] Header, byte[] Body)> _articles = new(StringComparer.Ordinal);
            private readonly Dictionary<string, byte[]> _data = new(StringComparer.Ordinal);
            private readonly List<TcpClient> _clients = [];
            private readonly TimeSpan _login;
            private readonly TimeSpan _roundTrip;
            private readonly long _bytesPerSecond;
            private readonly Task _accepting;
            private int _logins;
            private int _mostInFlight;

            public SlowNewsServer(TimeSpan login, TimeSpan roundTrip, long bytesPerSecond)
            {
                _login = login;
                _roundTrip = roundTrip;
                _bytesPerSecond = bytesPerSecond;
                _listener.Start(512);
                _accepting = AcceptAsync();
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public int Logins => Volatile.Read(ref _logins);
            public int MostInFlight => Volatile.Read(ref _mostInFlight);

            public byte[] Data(NzbFile file) => _data[file.FileName];

            // Every article carries the same bytes at its own place, so the server encodes one body only.
            public NzbFile Publish(string name, int parts)
            {
                var chunk = new byte[ArticleBytes];
                new Random(7).NextBytes(chunk);
                var encoded = Encoding.Latin1.GetBytes(Stuff(Body(chunk)));
                var size = (long)ArticleBytes * parts;
                var data = new byte[size];
                var segments = new List<NzbSegment>();
                for (var part = 0; part < parts; part++)
                {
                    chunk.CopyTo(data, (long)part * ArticleBytes);
                    var id = $"{name}.{part + 1}@speed.example.test";
                    var begin = (long)part * ArticleBytes + 1;
                    var header = Encoding.Latin1.GetBytes($"222 0 body\r\n=ybegin part={part + 1} line=128 size={size} name={name}\r\n=ypart begin={begin} end={begin + ArticleBytes - 1}\r\n");
                    lock (_articles) _articles[id] = (header, encoded);
                    segments.Add(new NzbSegment(part + 1, ArticleBytes, id));
                }

                _data[name] = data;
                return new NzbFile(name, segments);
            }

            public void DropAll()
            {
                lock (_clients)
                {
                    foreach (var client in _clients) client.Dispose();
                    _clients.Clear();
                }
            }

            private static string Body(byte[] slice)
            {
                var text = new StringBuilder();
                var column = 0;
                foreach (var value in slice)
                {
                    var encoded = (byte)(value + 42);
                    if (encoded is 0 or 10 or 13 or 61 || column == 0 && encoded == (byte)'.')
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

            private static string Stuff(string body) =>
                string.Join("\r\n", body.Split("\r\n").Select(line => line.StartsWith('.') ? "." + line : line)) + ".\r\n";

            private async Task AcceptAsync()
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    }
                    catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
                    {
                        return;
                    }

                    lock (_clients) _clients.Add(client);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }

            private async Task ServeAsync(TcpClient client)
            {
                var answers = Channel.CreateUnbounded<(long Due, byte[]? Header, byte[] Body)>();
                var inFlight = 0;
                try
                {
                    var stream = client.GetStream();
                    await Task.Delay(_login, _stop.Token);
                    await stream.WriteAsync("200 slow news ready\r\n"u8.ToArray(), _stop.Token);
                    var writing = Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (var answer in answers.Reader.ReadAllAsync(_stop.Token))
                            {
                                var wait = answer.Due - Stopwatch.GetTimestamp();
                                if (wait > 0) await Task.Delay(TimeSpan.FromTicks(wait * TimeSpan.TicksPerSecond / Stopwatch.Frequency), _stop.Token);
                                if (answer.Header is not null) await stream.WriteAsync(answer.Header, _stop.Token);
                                await WriteLimitedAsync(stream, answer.Body);
                                Interlocked.Decrement(ref inFlight);
                            }
                        }
                        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
                        {
                        }
                    });
                    using var reader = new StreamReader(stream, Encoding.Latin1);
                    while (await reader.ReadLineAsync(_stop.Token) is { } line)
                    {
                        var parts = line.Split(' ', 3);
                        var due = Stopwatch.GetTimestamp() + (long)(_roundTrip.TotalSeconds * Stopwatch.Frequency);
                        byte[]? header = null;
                        byte[] body;
                        switch (parts[0].ToUpperInvariant())
                        {
                            case "AUTHINFO" when parts[1] == "USER":
                                body = "381 password\r\n"u8.ToArray();
                                due += (long)(_login.TotalSeconds * Stopwatch.Frequency / 2);
                                break;
                            case "AUTHINFO":
                                Interlocked.Increment(ref _logins);
                                body = parts[2] == "news-secret" ? "281 welcome\r\n"u8.ToArray() : "481 no\r\n"u8.ToArray();
                                break;
                            case "BODY":
                                (byte[] Header, byte[] Body) article;
                                bool found;
                                lock (_articles) found = _articles.TryGetValue(parts[1].Trim('<', '>'), out article);
                                if (found)
                                {
                                    header = article.Header;
                                    body = article.Body;
                                }
                                else
                                {
                                    body = "430 no such article\r\n"u8.ToArray();
                                }

                                break;
                            case "QUIT":
                                body = "205 bye\r\n"u8.ToArray();
                                break;
                            default:
                                body = "500 what\r\n"u8.ToArray();
                                break;
                        }

                        var current = Interlocked.Increment(ref inFlight);
                        int most;
                        while (current > (most = Volatile.Read(ref _mostInFlight)) && Interlocked.CompareExchange(ref _mostInFlight, current, most) != most)
                        {
                        }

                        await answers.Writer.WriteAsync((due, header, body), _stop.Token);
                        if (parts[0].Equals("QUIT", StringComparison.OrdinalIgnoreCase)) break;
                    }

                    answers.Writer.TryComplete();
                    await writing;
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
                {
                }
                finally
                {
                    answers.Writer.TryComplete();
                    lock (_clients) _clients.Remove(client);
                    client.Dispose();
                }
            }

            // The line of one connection carries a limited speed.
            private async Task WriteLimitedAsync(NetworkStream stream, byte[] body)
            {
                if (_bytesPerSecond <= 0)
                {
                    await stream.WriteAsync(body, _stop.Token);
                    return;
                }

                const int Slice = 64 * 1024;
                var started = Stopwatch.GetTimestamp();
                for (var offset = 0; offset < body.Length; offset += Slice)
                {
                    var length = Math.Min(Slice, body.Length - offset);
                    await stream.WriteAsync(body.AsMemory(offset, length), _stop.Token);
                    var due = (double)(offset + length) / _bytesPerSecond - Stopwatch.GetElapsedTime(started).TotalSeconds;
                    if (due > 0) await Task.Delay(TimeSpan.FromSeconds(due), _stop.Token);
                }
            }

            public async ValueTask DisposeAsync()
            {
                await _stop.CancelAsync();
                _listener.Stop();
                await _accepting;
                DropAll();
                _stop.Dispose();
            }
        }
    }
}
