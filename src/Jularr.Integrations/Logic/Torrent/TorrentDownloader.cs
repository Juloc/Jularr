using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Jularr.Integrations.Data.Network;
using Jularr.Integrations.Data.Torrent;

namespace Jularr.Integrations.Logic.Torrent
{
    // Jularr's own download-only torrent engine. It never listens, never unchokes, never answers a request and announces nothing it has:
    // every byte it writes to a peer goes through TorrentOutboundWire. One pass downloads until its deadline, then closes every connection and
    // keeps the verified pieces in the job folder; the next pass, also after a restart, continues from them.
    internal static class TorrentDownloader
    {
        public const string TorrentName = "job.torrent";
        public const string MagnetName = "job.magnet";
        public const string InfoName = "job.info";
        public const string StateName = "job.state";
        public const string DataName = "data";
        private const int BlockLength = 16 * 1024;
        private const int Pipeline = 16;
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan AnnounceTimeout = TimeSpan.FromSeconds(15);
        private static long _receivedBytes;
        private static int _peers;

        // File data verified and written by this process, and the peers connected now.
        public static long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
        public static int ConnectedPeers => Volatile.Read(ref _peers);

        public static async Task<TorrentPassResult> RunAsync(TorrentPassRequest request, CancellationToken ct)
        {
            var directory = request.JobDirectory;
            var data = Path.Combine(directory, DataName);
            MagnetLink? magnet = File.Exists(Path.Combine(directory, MagnetName)) ? MagnetLink.Parse((await File.ReadAllTextAsync(Path.Combine(directory, MagnetName), ct)).Trim()) : null;
            TorrentMetainfo? meta = null;
            if (File.Exists(Path.Combine(directory, TorrentName))) meta = TorrentMetainfo.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, TorrentName), ct));
            else if (magnet is not null && File.Exists(Path.Combine(directory, InfoName))) meta = TorrentMetainfo.FromInfo(await File.ReadAllBytesAsync(Path.Combine(directory, InfoName), ct), magnet.Trackers);
            else if (magnet is null) return new TorrentPassResult(TorrentPassState.Failed, 0, 0, 0, 0, 0, false, "torrent_invalid");

            // A finished job contacts no peer at all.
            TorrentStorage? storage = null;
            if (meta is not null)
            {
                storage = new TorrentStorage(meta, data);
                await storage.LoadAsync(Path.Combine(directory, StateName), ct);
                if (storage.IsComplete) return Result(meta, storage, 0, 0, false, TorrentPassState.Completed, data);
            }

            var infoHash = meta?.InfoHash ?? magnet!.InfoHash;
            var peerId = Encoding.ASCII.GetBytes("-JL0100-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));
            var swarm = new Swarm(meta, storage, infoHash, request.Throttle);
            var peers = new List<(string Host, int Port)>(magnet?.Peers ?? []);
            foreach (var tracker in (meta?.Trackers ?? magnet!.Trackers).Where(tracker => tracker.Scheme is "http" or "https").Take(20))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(AnnounceTimeout);
                    peers.AddRange(await TorrentTrackers.AnnounceAsync(request.Trackers, tracker, infoHash, peerId, 0, storage?.RemainingBytes ?? 0, timeout.Token));
                }
                catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or IOException || exception is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    // One tracker that does not answer leaves the others.
                }
            }

            var candidates = new Queue<(string Host, int Port)>(peers.Distinct().Take(300));
            if (candidates.Count == 0) return meta is null ? new TorrentPassResult(TorrentPassState.Downloading, 0, 0, 0, 0, 0, false, "torrent_no_peers")
                : Result(meta, storage!, 0, 0, false, TorrentPassState.Downloading, null, "torrent_no_peers");

            // Up to the peer limit at once until the deadline, the end of the candidates or the end of the job.
            using var pass = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var left = request.Deadline - DateTimeOffset.UtcNow;
            pass.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            swarm.Finished = pass;
            var running = new List<Task<bool>>();
            var connected = 0;
            while (!pass.IsCancellationRequested && (candidates.Count > 0 || running.Count > 0))
            {
                while (running.Count < request.MaximumPeers && candidates.Count > 0) running.Add(SessionAsync(swarm, candidates.Dequeue(), request.Connect, peerId, pass.Token));
                var ended = await Task.WhenAny(running);
                running.Remove(ended);
                if (await ended) connected++;
            }

            await pass.CancelAsync();
            foreach (var session in running) await session;

            // A magnet's verified metadata is kept; the next pass downloads its files.
            if (meta is null)
            {
                if (swarm.Metadata is not { } info) return new TorrentPassResult(TorrentPassState.Downloading, 0, 0, 0, 0, connected, false, swarm.TunnelFailed ? "vpn_unavailable" : "torrent_no_metadata");
                await File.WriteAllBytesAsync(Path.Combine(directory, InfoName), info, ct);
                return new TorrentPassResult(TorrentPassState.Downloading, 0, 0, 0, 0, connected, true);
            }

            await storage!.SaveStateAsync(Path.Combine(directory, StateName), ct);
            var reason = swarm.Received > 0 ? null : swarm.TunnelFailed ? "vpn_unavailable" : connected == 0 ? "torrent_no_peers" : null;
            return Result(meta, storage, swarm.Received, connected, swarm.Received > 0, storage.IsComplete ? TorrentPassState.Completed : TorrentPassState.Downloading,
                storage.IsComplete ? data : null, reason);
        }

        private static TorrentPassResult Result(TorrentMetainfo meta, TorrentStorage storage, long received, int peers, bool progressed, TorrentPassState state, string? output,
            string? reason = null)
        {
            var remaining = storage.RemainingBytes;
            var percent = (int)((meta.TotalLength - remaining) * 100 / meta.TotalLength);
            return new TorrentPassResult(state, percent, meta.TotalLength, remaining, received, peers, progressed, reason, output);
        }

        // One peer: plain handshake (the extension bit only to fetch a magnet's metadata), interest and requests, nothing else ever leaves.
        private static async Task<bool> SessionAsync(Swarm swarm, (string Host, int Port) peer, TorrentConnector connect, byte[] peerId, CancellationToken ct)
        {
            Stream stream;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ConnectTimeout);
                stream = await connect(peer.Host, peer.Port, timeout.Token);
            }
            catch (TunnelUnavailableException)
            {
                swarm.TunnelFailed = true;
                return false;
            }
            catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
            {
                return false;
            }

            Interlocked.Increment(ref _peers);
            var state = new PeerState(swarm.PieceCount);
            try
            {
                await using var wire = new TorrentPeerWire(stream);
                var handshake = new byte[68];
                handshake[0] = 19;
                Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);
                if (swarm.NeedsMetadata) handshake[25] = 0x10;
                swarm.InfoHash.CopyTo(handshake, 28);
                peerId.CopyTo(handshake, 48);
                await wire.SendHandshakeAsync(handshake, ct);

                var answer = await ReadAsync(wire.ReadHandshakeAsync, ct);
                if (answer[0] != 19 || !answer.AsSpan(28, 20).SequenceEqual(swarm.InfoHash)) return true;
                if (swarm.NeedsMetadata)
                {
                    if ((answer[25] & 0x10) == 0) return true;
                    await wire.SendAsync(Frame(20, [0, .. TorrentOutboundWire.MetadataExtensionHandshake]), ct);
                }

                while (!ct.IsCancellationRequested && !swarm.IsDone)
                {
                    var message = await ReadAsync(wire.ReadMessageAsync, ct);
                    if (message.Length == 0) continue;
                    if (!await HandleAsync(swarm, state, message, ct)) return true;
                    await PumpAsync(swarm, state, wire, ct);
                }

                return true;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or System.Net.Sockets.SocketException or OperationCanceledException or EndOfStreamException)
            {
                return true;
            }
            finally
            {
                swarm.Release(state);
                Interlocked.Decrement(ref _peers);
            }
        }

        private static async Task<byte[]> ReadAsync(Func<CancellationToken, Task<byte[]>> read, CancellationToken ct)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleTimeout);
            return await read(idle.Token);
        }

        // What the peer said; false ends the connection (a piece that failed its check).
        private static async Task<bool> HandleAsync(Swarm swarm, PeerState state, byte[] message, CancellationToken ct)
        {
            switch (message[0])
            {
                case 0:
                    state.Choked = true;
                    swarm.Unassign(state);
                    return true;
                case 1:
                    state.Choked = false;
                    return true;
                case 4 when message.Length == 5:
                    var have = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(1));
                    if (have >= 0 && have < state.Has.Length) state.Has[have] = true;
                    return true;
                case 5:
                    for (var index = 0; index < state.Has.Length && index / 8 < message.Length - 1; index++) state.Has[index] = (message[1 + index / 8] & (0x80 >> (index % 8))) != 0;
                    return true;
                case 14:
                    Array.Fill(state.Has, true);
                    return true;
                case 7 when message.Length > 9:
                    return await swarm.ReceiveBlockAsync(state, BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(1)), BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(5)), message.AsMemory(9), ct);
                case 20 when message.Length > 2:
                    swarm.ReceiveExtension(state, message);
                    return true;
                default:
                    // Requests, interest and anything else from the peer get no answer: Jularr never serves.
                    return true;
            }
        }

        // Interest once the peer has something missing, then requests for one piece at a time.
        private static async Task PumpAsync(Swarm swarm, PeerState state, TorrentPeerWire wire, CancellationToken ct)
        {
            if (swarm.NeedsMetadata)
            {
                if (state.MetadataId is { } id && !state.MetadataPending && swarm.NextMetadataPiece() is { } metadataPiece)
                {
                    state.MetadataPending = true;
                    await wire.SendAsync(Frame(20, [id, .. Encoding.ASCII.GetBytes("d8:msg_typei0e5:piecei" + metadataPiece + "ee")]), ct);
                }

                return;
            }

            if (!state.Interested && swarm.Wants(state))
            {
                state.Interested = true;
                await wire.SendAsync(Frame(2, []), ct);
            }

            if (state.Choked) return;
            if (state.Piece is null && swarm.Assign(state) is { } piece) state.Start(piece, swarm.PieceSize(piece));
            if (state.Piece is not { } current) return;
            while (state.Outstanding < Pipeline && state.NextBlock < state.Buffer!.Length)
            {
                var length = Math.Min(BlockLength, state.Buffer.Length - state.NextBlock);
                var request = new byte[12];
                BinaryPrimitives.WriteInt32BigEndian(request, current);
                BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(4), state.NextBlock);
                BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(8), length);
                await wire.SendAsync(Frame(6, request), ct);
                state.NextBlock += length;
                state.Outstanding++;
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

        private sealed class PeerState(int pieceCount)
        {
            public bool[] Has { get; } = new bool[pieceCount];
            public bool Choked { get; set; } = true;
            public bool Interested { get; set; }
            public int? Piece { get; private set; }
            public byte[]? Buffer { get; private set; }
            public int NextBlock { get; set; }
            public int Received { get; set; }
            public int Outstanding { get; set; }
            public byte? MetadataId { get; set; }
            public bool MetadataPending { get; set; }

            public void Start(int piece, int size)
            {
                Piece = piece;
                Buffer = new byte[size];
                NextBlock = 0;
                Received = 0;
                Outstanding = 0;
            }

            public void Clear()
            {
                Piece = null;
                Buffer = null;
                Outstanding = 0;
            }
        }

        // What the connections of one pass share: which pieces are taken, the storage, and a magnet's metadata as it arrives.
        private sealed class Swarm(TorrentMetainfo? meta, TorrentStorage? storage, byte[] infoHash, Func<long, CancellationToken, ValueTask>? throttle = null)
        {
            private const int MetadataPieceLength = 16 * 1024;
            private const int MaximumMetadata = 8 * 1024 * 1024;
            private readonly Lock _lock = new();
            private readonly bool[] _taken = new bool[meta?.PieceCount ?? 0];
            private byte[]? _metadata;
            private bool[]? _metadataHave;
            private bool[]? _metadataAsked;
            private long _received;

            public byte[] InfoHash { get; } = infoHash;
            public CancellationTokenSource? Finished { get; set; }
            public bool TunnelFailed { get; set; }
            public byte[]? Metadata { get; private set; }
            public bool NeedsMetadata => meta is null;
            public int PieceCount => meta?.PieceCount ?? 0;
            public long Received => Interlocked.Read(ref _received);
            public bool IsDone => meta is null ? Metadata is not null : storage!.IsComplete;

            public int PieceSize(int index) => meta!.PieceSize(index);

            public bool Wants(PeerState peer)
            {
                for (var index = 0; index < peer.Has.Length; index++)
                {
                    if (peer.Has[index] && !storage!.Has(index)) return true;
                }

                return false;
            }

            public int? Assign(PeerState peer)
            {
                lock (_lock)
                {
                    var count = _taken.Length;
                    var start = Random.Shared.Next(count);
                    for (var step = 0; step < count; step++)
                    {
                        var index = (start + step) % count;
                        if (!peer.Has[index] || _taken[index] || storage!.Has(index)) continue;
                        _taken[index] = true;
                        return index;
                    }

                    return null;
                }
            }

            public void Unassign(PeerState peer)
            {
                if (peer.Piece is { } piece)
                {
                    lock (_lock) _taken[piece] = false;
                }

                peer.Clear();
            }

            public void Release(PeerState peer)
            {
                Unassign(peer);
                if (peer.MetadataPending) peer.MetadataPending = false;
            }

            // A block of the piece this peer was asked for; a piece that fails its check ends the connection.
            public async Task<bool> ReceiveBlockAsync(PeerState peer, int piece, int offset, ReadOnlyMemory<byte> block, CancellationToken ct)
            {
                if (peer.Piece != piece || peer.Buffer is null || offset < 0 || offset + block.Length > peer.Buffer.Length || block.Length == 0) return true;
                if (throttle is not null) await throttle(block.Length, ct);
                block.CopyTo(peer.Buffer.AsMemory(offset));
                peer.Received += block.Length;
                peer.Outstanding--;
                if (peer.Received < peer.Buffer.Length) return true;

                var written = await storage!.WritePieceAsync(piece, peer.Buffer, ct);
                if (written)
                {
                    Interlocked.Add(ref _received, peer.Buffer.Length);
                    Interlocked.Add(ref _receivedBytes, peer.Buffer.Length);
                }

                Unassign(peer);
                if (storage.IsComplete) Finished?.Cancel();
                return written;
            }

            // The peer's extension handshake names its ut_metadata number and the metadata size; data messages bring the pieces.
            public void ReceiveExtension(PeerState peer, byte[] message)
            {
                if (Bencode.DecodePrefix(message.AsSpan(2), out var length) is not Bencode.Dictionary dictionary) return;
                if (message[1] == 0)
                {
                    if (dictionary.GetValueOrDefault("m") is Bencode.Dictionary names && names.GetValueOrDefault("ut_metadata") is long id and > 0 and < 256) peer.MetadataId = (byte)id;
                    if (meta is null && dictionary.GetValueOrDefault("metadata_size") is long size and > 0 and <= MaximumMetadata)
                    {
                        lock (_lock)
                        {
                            if (_metadata is null)
                            {
                                _metadata = new byte[size];
                                var pieces = (int)((size + MetadataPieceLength - 1) / MetadataPieceLength);
                                _metadataHave = new bool[pieces];
                                _metadataAsked = new bool[pieces];
                            }
                        }
                    }

                    return;
                }

                // Jularr named ut_metadata 1 in its handshake.
                if (message[1] != 1 || meta is not null) return;
                peer.MetadataPending = false;
                if (dictionary.GetValueOrDefault("msg_type") is not 1L || dictionary.GetValueOrDefault("piece") is not long piece) return;
                lock (_lock)
                {
                    if (_metadata is null || piece < 0 || piece >= _metadataHave!.Length || _metadataHave[piece]) return;
                    var data = message.AsSpan(2 + length);
                    var expected = (int)Math.Min(MetadataPieceLength, _metadata.Length - piece * MetadataPieceLength);
                    if (data.Length != expected) return;
                    data.CopyTo(_metadata.AsSpan((int)piece * MetadataPieceLength));
                    _metadataHave[piece] = true;
                    if (!_metadataHave.All(have => have)) return;

                    // The metadata must hash to the magnet's info hash, else it is thrown away and fetched again.
                    if (SHA1.HashData(_metadata).AsSpan().SequenceEqual(InfoHash))
                    {
                        Metadata = _metadata;
                        Finished?.Cancel();
                    }
                    else
                    {
                        Array.Clear(_metadataHave);
                        Array.Clear(_metadataAsked!);
                    }
                }
            }

            public int? NextMetadataPiece()
            {
                lock (_lock)
                {
                    if (_metadataAsked is null) return null;
                    var next = Array.FindIndex(_metadataAsked, asked => !asked);
                    if (next < 0) next = Array.FindIndex(_metadataHave!, have => !have);
                    if (next < 0) return null;
                    _metadataAsked[next] = true;
                    return next;
                }
            }
        }
    }
}
