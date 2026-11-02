using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Jularr.Integrations.Data.Torrent;
using Jularr.Integrations.Logic.Torrent;

namespace Jularr.Backend.Tests
{
    // The native engine against real loopback sockets: a seeder that also asks for blocks and a counter-peer that only asks. Both log every byte the
    // engine sends; the log is parsed frame by frame and must contain no file data and no piece advertisement during, after and across passes.
    [TestClass]
    public sealed class TorrentEngineTests
    {
        private const int PieceLength = 32 * 1024;

        [TestMethod]
        public async Task Torrent_DownloadsCompletely_AndSendsNoFileDataDuringAfterOrAcrossPasses()
        {
            var job = NewJob();
            var payload = RandomNumberGenerator.GetBytes(13 * PieceLength - 5000);
            var (torrent, info) = Torrent(payload, "http://tracker.example.test/announce");
            await File.WriteAllBytesAsync(Path.Combine(job, TorrentDownloader.TorrentName), torrent);
            var hash = SHA1.HashData(info);

            // The first pass meets a seeder that has only the first half; the counter-peer has nothing and only asks.
            await using var partial = new Peer(hash, payload, info, piece => piece < 6);
            await using var asker = new Peer(hash, null, null, _ => false);
            using var tracker = Tracker([partial.EndPoint, asker.EndPoint]);
            var first = await TorrentDownloader.RunAsync(new TorrentPassRequest(job, Connect, tracker, DateTimeOffset.UtcNow.AddSeconds(4)), CancellationToken.None);
            Assert.AreEqual(TorrentPassState.Downloading, first.State, first.ReasonKey);
            Assert.IsTrue(first.Progressed && first.ReceivedBytes == 6 * PieceLength, "Half the pieces arrive from the partial seeder.");

            // The next pass, like after a restart, continues from the verified pieces on disk with a full seeder.
            await using var full = new Peer(hash, payload, info, _ => true);
            using var tracker2 = Tracker([full.EndPoint, asker.EndPoint]);
            var second = await TorrentDownloader.RunAsync(new TorrentPassRequest(job, Connect, tracker2, DateTimeOffset.UtcNow.AddSeconds(20)), CancellationToken.None);
            Assert.AreEqual(TorrentPassState.Completed, second.State, second.ReasonKey);
            Assert.AreEqual(payload.Length - 6L * PieceLength, second.ReceivedBytes);
            CollectionAssert.AreEqual(payload, Join(job, "release", "a.bin", "b.bin"));

            // Completed: further passes, with or without the state file, verify from disk and contact nobody.
            var connections = full.Connections + asker.Connections;
            Assert.AreEqual(TorrentPassState.Completed, (await TorrentDownloader.RunAsync(new TorrentPassRequest(job, Connect, tracker2, DateTimeOffset.UtcNow.AddSeconds(5)), CancellationToken.None)).State);
            File.Delete(Path.Combine(job, TorrentDownloader.StateName));
            Assert.AreEqual(TorrentPassState.Completed, (await TorrentDownloader.RunAsync(new TorrentPassRequest(job, Connect, tracker2, DateTimeOffset.UtcNow.AddSeconds(5)), CancellationToken.None)).State);
            Assert.AreEqual(connections, full.Connections + asker.Connections);

            // Every peer asked for blocks; the engine answered none of them.
            Assert.IsTrue(asker.Connections >= 2 && asker.RequestsSent > 0 && partial.RequestsSent > 0 && full.RequestsSent > 0);
            foreach (var peer in new[] { partial, asker, full }) AssertDownloadOnly(peer.Received);
        }

        [TestMethod]
        public async Task Magnet_FetchesVerifiedMetadataThenFiles_AndOnlyRequestsMetadata()
        {
            var job = NewJob();
            var payload = RandomNumberGenerator.GetBytes(5 * PieceLength);
            var (_, info) = Torrent(payload, null);
            var hash = SHA1.HashData(info);
            await using var seeder = new Peer(hash, payload, info, _ => true);
            await using var asker = new Peer(hash, null, null, _ => false);
            await File.WriteAllTextAsync(Path.Combine(job, TorrentDownloader.MagnetName),
                "magnet:?xt=urn:btih:" + Convert.ToHexString(hash) + "&dn=release&x.pe=127.0.0.1:" + asker.EndPoint.Port + "&x.pe=127.0.0.1:" + seeder.EndPoint.Port);
            using var noTracker = Tracker([]);

            var metadata = await TorrentDownloader.RunAsync(new TorrentPassRequest(job, Connect, noTracker, DateTimeOffset.UtcNow.AddSeconds(10)), CancellationToken.None);
            Assert.IsTrue(metadata.Progressed, metadata.ReasonKey);
            CollectionAssert.AreEqual(info, await File.ReadAllBytesAsync(Path.Combine(job, TorrentDownloader.InfoName)));

            var files = await TorrentDownloader.RunAsync(new TorrentPassRequest(job, Connect, noTracker, DateTimeOffset.UtcNow.AddSeconds(20)), CancellationToken.None);
            Assert.AreEqual(TorrentPassState.Completed, files.State, files.ReasonKey);
            CollectionAssert.AreEqual(payload, Join(job, "release", "a.bin", "b.bin"));
            Assert.IsTrue(seeder.MetadataRequests > 0 && asker.RequestsSent > 0);
            AssertDownloadOnly(seeder.Received);
            AssertDownloadOnly(asker.Received);
        }

        [TestMethod]
        public async Task Lock_RefusesEveryFrameThatCarriesOrAdvertisesData()
        {
            Assert.IsTrue(await TorrentOutboundWire.VerifyLockAsync(CancellationToken.None));

            var handshake = new byte[68];
            handshake[0] = 19;
            Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);
            await using var outbound = new MemoryStream();
            handshake[25] = 0x10;
            await TorrentOutboundWire.SendHandshakeAsync(outbound, handshake, CancellationToken.None);
            foreach (var (index, bit) in new[] { (27, (byte)0x04), (27, (byte)0x01), (25, (byte)0x11) })
            {
                var other = (byte[])handshake.Clone();
                other[25] = 0;
                other[index] = bit;
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => TorrentOutboundWire.SendHandshakeAsync(outbound, other, CancellationToken.None));
            }

            await TorrentOutboundWire.SendMessageAsync(outbound, Frame(20, [3, .. "d8:msg_typei0e5:piecei2ee"u8.ToArray()]), CancellationToken.None);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => TorrentOutboundWire.SendMessageAsync(outbound, Frame(20, [3, .. "d8:msg_typei1e5:piecei2ee"u8.ToArray()]), CancellationToken.None));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => TorrentOutboundWire.SendMessageAsync(outbound, Frame(20, [0, .. "d1:md11:ut_metadatai1ee13:metadata_sizei9ee"u8.ToArray()]), CancellationToken.None));
            Assert.AreEqual(68L + 4 + 1 + 1 + 25, outbound.Length, "Only the allowed handshake and request were written.");
        }

        [TestMethod]
        public void Metainfo_RejectsPathsOutsideTheJob()
        {
            var info = Bencode.Encode(new Dictionary<string, object>
            {
                ["name"] = "release",
                ["piece length"] = (long)PieceLength,
                ["pieces"] = new byte[20],
                ["files"] = new List<object> { new Dictionary<string, object> { ["length"] = 10L, ["path"] = new List<object> { "..", "evil" } } }
            });

            Assert.ThrowsExactly<InvalidDataException>(() => TorrentMetainfo.FromInfo(info, []));
            var hex = MagnetLink.Parse("magnet:?xt=urn:btih:1220a4fdbd4b399a3e98d3d6f4f7e0f13c3c3b1e&dn=x&tr=udp%3A%2F%2Ftracker.example.test%3A80");
            var base32 = MagnetLink.Parse("magnet:?xt=urn:btih:CIQKJ7N5JM4ZUPUY2PLPJ57A6E6DYOY6");
            CollectionAssert.AreEqual(hex.InfoHash, base32.InfoHash);
            Assert.AreEqual("udp", hex.Trackers.Single().Scheme);
        }

        // Parses what a peer received from the engine: one allowed handshake, then only control frames and metadata requests.
        private static void AssertDownloadOnly(ConcurrentQueue<byte[]> connections)
        {
            Assert.IsFalse(connections.IsEmpty);
            foreach (var bytes in connections)
            {
                Assert.IsTrue(bytes.Length >= 68);
                Assert.IsTrue(bytes.AsSpan(20, 8).IndexOfAnyExcept((byte)0) < 0 || bytes[25] == 0x10 && bytes.AsSpan(20, 8).Count((byte)0) == 7);
                var position = 68;
                while (position + 4 <= bytes.Length)
                {
                    var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(position));
                    if (length > 0 && position + 4 < bytes.Length)
                    {
                        var type = bytes[position + 4];
                        Assert.IsTrue(type is 2 or 3 or 6 or 8 or 20, "Outbound frame type " + type + " must never be sent.");
                        if (type == 20 && position + 4 + length <= bytes.Length)
                        {
                            var body = Encoding.ASCII.GetString(bytes, position + 6, length - 2);
                            Assert.IsTrue(bytes[position + 5] == 0 ? body == "d1:md11:ut_metadatai1eee" : body.StartsWith("d8:msg_typei0e5:piecei", StringComparison.Ordinal), body);
                        }
                    }

                    position += 4 + length;
                }
            }
        }

        private static async Task<Stream> Connect(string host, int port, CancellationToken ct)
        {
            var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);
            return client.GetStream();
        }

        private static string NewJob()
        {
            var job = Path.Combine(Path.GetTempPath(), "jularr-torrent-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            return job;
        }

        private static byte[] Join(string job, string name, params string[] files) =>
            [.. files.SelectMany(file => File.ReadAllBytes(Path.Combine(job, TorrentDownloader.DataName, name, file)))];

        // Two files of a release; the first ends inside a piece.
        private static (byte[] Torrent, byte[] Info) Torrent(byte[] payload, string? announce)
        {
            var pieces = new List<byte>();
            for (var offset = 0; offset < payload.Length; offset += PieceLength) pieces.AddRange(SHA1.HashData(payload.AsSpan(offset, Math.Min(PieceLength, payload.Length - offset))));
            var first = payload.Length * 3 / 4 + 7;
            var info = Bencode.Encode(new Dictionary<string, object>
            {
                ["name"] = "release",
                ["piece length"] = (long)PieceLength,
                ["pieces"] = pieces.ToArray(),
                ["files"] = new List<object>
                {
                    new Dictionary<string, object> { ["length"] = (long)first, ["path"] = new List<object> { "a.bin" } },
                    new Dictionary<string, object> { ["length"] = (long)(payload.Length - first), ["path"] = new List<object> { "b.bin" } }
                }
            });
            var root = new Dictionary<string, object>();
            if (announce is not null) root["announce"] = announce;
            using var torrent = new MemoryStream();
            torrent.Write(Bencode.Encode(root)[..^1]);
            torrent.Write("4:info"u8);
            torrent.Write(info);
            torrent.WriteByte((byte)'e');
            return (torrent.ToArray(), info);
        }

        // A tracker that names the given peers and checks that the engine reports no upload.
        private static HttpClient Tracker(IPEndPoint[] peers) => new(new TrackerHandler(peers));

        private sealed class TrackerHandler(IPEndPoint[] peers) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                StringAssert.Contains(request.RequestUri!.Query, "uploaded=0");
                var compact = peers.SelectMany(peer => (byte[])[.. peer.Address.GetAddressBytes(), (byte)(peer.Port >> 8), (byte)peer.Port]).ToArray();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bencode.Encode(new Dictionary<string, object> { ["interval"] = 60L, ["peers"] = compact })) });
            }
        }

        private static byte[] Frame(byte type, byte[] payload)
        {
            var frame = new byte[5 + payload.Length];
            BinaryPrimitives.WriteInt32BigEndian(frame, 1 + payload.Length);
            frame[4] = type;
            payload.CopyTo(frame, 5);
            return frame;
        }

        // A controlled peer on loopback. With data it seeds the pieces it has and the metadata; with or without, it unchokes, says it is interested,
        // advertises nothing it lacks and keeps asking the engine for blocks. It logs every byte the engine sends.
        private sealed class Peer : IAsyncDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _accept;
            private readonly byte[] _hash;
            private readonly byte[]? _payload;
            private readonly byte[]? _info;
            private readonly Func<int, bool> _has;
            private int _connections;
            private int _requests;
            private int _metadataRequests;

            public Peer(byte[] hash, byte[]? payload, byte[]? info, Func<int, bool> has)
            {
                _hash = hash;
                _payload = payload;
                _info = info;
                _has = has;
                _listener.Start();
                _accept = Task.Run(AcceptAsync);
            }

            public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;
            public ConcurrentQueue<byte[]> Received { get; } = new();
            public int Connections => Volatile.Read(ref _connections);
            public int RequestsSent => Volatile.Read(ref _requests);
            public int MetadataRequests => Volatile.Read(ref _metadataRequests);

            private async Task AcceptAsync()
            {
                var sessions = new List<Task>();
                try
                {
                    while (true) sessions.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                }

                await Task.WhenAll(sessions);
            }

            private async Task ServeAsync(TcpClient client)
            {
                Interlocked.Increment(ref _connections);
                var log = new MemoryStream();
                using (client)
                {
                    var stream = client.GetStream();
                    try
                    {
                        var handshake = new byte[68];
                        await stream.ReadExactlyAsync(handshake, _stop.Token);
                        log.Write(handshake);
                        var reply = (byte[])handshake.Clone();
                        reply[25] = 0x10;
                        RandomNumberGenerator.Fill(reply.AsSpan(48));
                        await stream.WriteAsync(reply, _stop.Token);

                        // Its pieces, its metadata, unchoked, interested, and a first request for a block of the engine.
                        var count = _payload is null ? 13 : (_payload.Length + PieceLength - 1) / PieceLength;
                        var bitfield = new byte[(count + 7) / 8];
                        for (var piece = 0; piece < count; piece++) if (_payload is not null && _has(piece)) bitfield[piece / 8] |= (byte)(0x80 >> (piece % 8));
                        await stream.WriteAsync(Frame(5, bitfield), _stop.Token);
                        var handshakeDictionary = new Dictionary<string, object> { ["m"] = new Dictionary<string, object> { ["ut_metadata"] = 3L } };
                        if (_info is not null) handshakeDictionary["metadata_size"] = (long)_info.Length;
                        await stream.WriteAsync(Frame(20, [0, .. Bencode.Encode(handshakeDictionary)]), _stop.Token);
                        await stream.WriteAsync(Frame(1, []), _stop.Token);
                        await stream.WriteAsync(Frame(2, []), _stop.Token);
                        await AskAsync(stream);

                        var header = new byte[4];
                        while (true)
                        {
                            await stream.ReadExactlyAsync(header, _stop.Token);
                            var length = BinaryPrimitives.ReadInt32BigEndian(header);
                            var message = new byte[length];
                            await stream.ReadExactlyAsync(message, _stop.Token);
                            log.Write(header);
                            log.Write(message);
                            if (length == 0) continue;

                            if (message[0] == 6 && _payload is not null)
                            {
                                var piece = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(1));
                                var begin = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(5));
                                var size = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(9));
                                if (!_has(piece)) continue;
                                var block = new byte[8 + size];
                                BinaryPrimitives.WriteInt32BigEndian(block, piece);
                                BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(4), begin);
                                _payload.AsSpan(piece * PieceLength + begin, size).CopyTo(block.AsSpan(8));
                                await stream.WriteAsync(Frame(7, block), _stop.Token);
                                await AskAsync(stream);
                            }
                            else if (message[0] == 20 && message[1] == 3 && _info is not null)
                            {
                                Interlocked.Increment(ref _metadataRequests);
                                var request = (Bencode.Dictionary)Bencode.Decode(message.AsSpan(2));
                                var piece = (int)(long)request["piece"];
                                var data = _info.AsSpan(piece * 16384, Math.Min(16384, _info.Length - piece * 16384)).ToArray();
                                var head = Bencode.Encode(new Dictionary<string, object> { ["msg_type"] = 1L, ["piece"] = (long)piece, ["total_size"] = (long)_info.Length });
                                await stream.WriteAsync(Frame(20, [1, .. head, .. data]), _stop.Token);
                            }
                            else
                            {
                                await AskAsync(stream);
                            }
                        }
                    }
                    catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException or EndOfStreamException or ObjectDisposedException)
                    {
                    }
                    finally
                    {
                        Received.Enqueue(log.ToArray());
                    }
                }
            }

            // A request for a block the engine may already have: it must never be answered.
            private async Task AskAsync(NetworkStream stream)
            {
                var request = new byte[12];
                BinaryPrimitives.WriteInt32BigEndian(request, Random.Shared.Next(5));
                BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(8), 16384);
                await stream.WriteAsync(Frame(6, request), _stop.Token);
                await stream.WriteAsync(Frame(20, [1, .. "d8:msg_typei0e5:piecei0ee"u8.ToArray()]), _stop.Token);
                Interlocked.Increment(ref _requests);
            }

            public async ValueTask DisposeAsync()
            {
                await _stop.CancelAsync();
                _listener.Stop();
                await _accept;
            }
        }
    }
}
