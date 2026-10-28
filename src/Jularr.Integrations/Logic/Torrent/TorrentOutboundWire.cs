using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Jularr.Integrations.Logic.Torrent
{
    // All outbound BitTorrent peer-wire traffic must pass this deny-by-default boundary.
    // No file data, piece advertisements or unreviewed extensions may leave this writer.
    internal static partial class TorrentOutboundWire
    {
        private const int MaximumBlockLength = 16 * 1024;
        private const int HandshakeLength = 68;
        private static readonly byte[] ProtocolName = Encoding.ASCII.GetBytes("BitTorrent protocol");

        // The only extension handshake Jularr sends: it offers ut_metadata to fetch a magnet's metadata and nothing else (no metadata size, no peer exchange).
        public static readonly byte[] MetadataExtensionHandshake = Encoding.ASCII.GetBytes("d1:md11:ut_metadatai1eee");

        private static long _checkedFrames;
        private static long _blockedFrames;
        private static long _sentBytes;
        private static long _sentPieceBytes;

        // What this process's writer let through and refused; file data has no path through it.
        public static long CheckedFrames => Interlocked.Read(ref _checkedFrames);
        public static long BlockedFrames => Interlocked.Read(ref _blockedFrames);
        public static long SentBytes => Interlocked.Read(ref _sentBytes);
        public static long SentPieceBytes => Interlocked.Read(ref _sentPieceBytes);

        public static async Task SendHandshakeAsync(Stream stream, ReadOnlyMemory<byte> handshake, CancellationToken ct)
        {
            Interlocked.Increment(ref _checkedFrames);
            if (!IsAllowedHandshake(handshake.Span))
            {
                Interlocked.Increment(ref _blockedFrames);
                throw new InvalidDataException("Unsupported outbound BitTorrent handshake.");
            }

            await stream.WriteAsync(handshake, ct);
            Interlocked.Add(ref _sentBytes, handshake.Length);
        }

        public static async Task SendMessageAsync(Stream stream, ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            Interlocked.Increment(ref _checkedFrames);
            try
            {
                ValidateMessage(frame.Span);
            }
            catch (InvalidDataException)
            {
                Interlocked.Increment(ref _blockedFrames);
                throw;
            }

            await stream.WriteAsync(frame, ct);
            Interlocked.Add(ref _sentBytes, frame.Length);
            if (frame.Length > 4 && frame.Span[4] == 7) Interlocked.Add(ref _sentPieceBytes, frame.Length);
        }

        // Proves the lock in this process now: every frame that would carry or advertise file data goes to the writer, is refused and leaves the stream empty.
        public static async Task<bool> VerifyLockAsync(CancellationToken ct)
        {
            byte[][] forbidden =
            [
                Frame(7, new byte[8 + MaximumBlockLength]),
                Frame(1, []),
                Frame(4, new byte[4]),
                Frame(5, new byte[4]),
                Frame(14, []),
                Frame(17, new byte[4]),
                Frame(20, [1, .. "d8:msg_typei1e5:piecei0e10:total_sizei1ee"u8.ToArray()]),
                Frame(20, [0, .. "d1:md6:ut_pexi2eee"u8.ToArray()])
            ];
            await using var probe = new MemoryStream();
            foreach (var frame in forbidden)
            {
                try
                {
                    await SendMessageAsync(probe, frame, ct);
                    return false;
                }
                catch (InvalidDataException)
                {
                }
            }

            return probe.Length == 0;
        }

        private static bool IsAllowedHandshake(ReadOnlySpan<byte> handshake)
        {
            if (handshake.Length != HandshakeLength || handshake[0] != ProtocolName.Length || !handshake.Slice(1, ProtocolName.Length).SequenceEqual(ProtocolName))
            {
                return false;
            }

            // The plain handshake, or one that announces the extension protocol alone (no fast extension, no DHT).
            var reserved = handshake.Slice(20, 8);
            return reserved.IndexOfAnyExcept((byte)0) < 0
                || reserved[5] == 0x10 && reserved[..5].IndexOfAnyExcept((byte)0) < 0 && reserved[6..].IndexOfAnyExcept((byte)0) < 0;
        }

        private static byte[] Frame(byte type, byte[] payload)
        {
            var frame = new byte[5 + payload.Length];
            BinaryPrimitives.WriteInt32BigEndian(frame, 1 + payload.Length);
            frame[4] = type;
            payload.CopyTo(frame, 5);
            return frame;
        }

        private static void ValidateMessage(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 4)
            {
                throw new InvalidDataException("Incomplete outbound BitTorrent frame.");
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(frame);
            if (length < 0 || length != frame.Length - 4)
            {
                throw new InvalidDataException("Invalid outbound BitTorrent frame length.");
            }

            if (length == 0)
            {
                return; // Keep-alive contains no payload.
            }

            var type = frame[4];
            if (length == 1 && type is 0 or 2 or 3 or 15)
            {
                return; // Choke, interested, not interested, have-none.
            }

            if (length == 13 && type is 6 or 8 or 16)
            {
                var pieceIndex = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(5, 4));
                var offset = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(9, 4));
                var blockLength = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(13, 4));
                if (pieceIndex >= 0 && offset >= 0 && blockLength > 0 && blockLength <= MaximumBlockLength && offset <= int.MaxValue - blockLength)
                {
                    return; // Request, cancel or reject request; no file data.
                }
            }

            if (type == 20 && length >= 2)
            {
                // The fixed extension handshake, and requests for a piece of a magnet's metadata; never metadata, peers or anything else.
                var payload = frame[6..];
                if (frame[5] == 0 && payload.SequenceEqual(MetadataExtensionHandshake)) return;
                if (frame[5] != 0 && payload.Length <= 40 && MetadataRequest().IsMatch(Encoding.ASCII.GetString(payload))) return;
            }

            // Piece (7), unchoke (1), have/bitfield, fast extension, other extension messages and every unknown type fail closed.
            throw new InvalidDataException("Outbound BitTorrent message is blocked by the download-only policy.");
        }

        [GeneratedRegex("^d8:msg_typei0e5:piecei(0|[1-9][0-9]{0,5})ee$", RegexOptions.CultureInvariant)]
        private static partial Regex MetadataRequest();
    }

    // One peer connection: it reads freely and writes only through the download-only boundary. The engine holds no other reference to the stream.
    internal sealed class TorrentPeerWire(Stream stream) : IAsyncDisposable
    {
        private const int MaximumFrameLength = 1024 * 1024;
        private readonly byte[] _header = new byte[4];

        public Task SendHandshakeAsync(ReadOnlyMemory<byte> handshake, CancellationToken ct) => TorrentOutboundWire.SendHandshakeAsync(stream, handshake, ct);

        public Task SendAsync(byte[] frame, CancellationToken ct) => TorrentOutboundWire.SendMessageAsync(stream, frame, ct);

        public async Task<byte[]> ReadHandshakeAsync(CancellationToken ct)
        {
            var handshake = new byte[68];
            await stream.ReadExactlyAsync(handshake, ct);
            return handshake;
        }

        // The next message without its length prefix; an empty array is a keep-alive.
        public async Task<byte[]> ReadMessageAsync(CancellationToken ct)
        {
            await stream.ReadExactlyAsync(_header, ct);
            var length = BinaryPrimitives.ReadInt32BigEndian(_header);
            if (length is < 0 or > MaximumFrameLength) throw new InvalidDataException("Inbound BitTorrent frame is too large.");
            var message = new byte[length];
            await stream.ReadExactlyAsync(message, ct);
            return message;
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
