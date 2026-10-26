using System.Buffers.Binary;
using System.Text;

namespace Jularr.Integrations.Logic.Torrent
{
    // All outbound BitTorrent peer-wire traffic must pass this deny-by-default boundary.
    // No file data, piece advertisements or unreviewed extensions may leave this writer.
    internal static class TorrentOutboundWire
    {
        private const int MaximumBlockLength = 16 * 1024;
        private const int HandshakeLength = 68;
        private static readonly byte[] ProtocolName = Encoding.ASCII.GetBytes("BitTorrent protocol");

        public static async Task SendHandshakeAsync(Stream stream, ReadOnlyMemory<byte> handshake, CancellationToken ct)
        {
            if (handshake.Length != HandshakeLength || handshake.Span[0] != ProtocolName.Length
                || !handshake.Span.Slice(1, ProtocolName.Length).SequenceEqual(ProtocolName)
                || handshake.Span.Slice(20, 8).IndexOfAnyExcept((byte)0) >= 0)
            {
                throw new InvalidDataException("Unsupported outbound BitTorrent handshake.");
            }

            await stream.WriteAsync(handshake, ct);
        }

        public static async Task SendMessageAsync(Stream stream, ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            ValidateMessage(frame.Span);
            await stream.WriteAsync(frame, ct);
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

            // Piece (7), unchoke (1), have/bitfield, extension messages and every unknown type fail closed.
            throw new InvalidDataException("Outbound BitTorrent message is blocked by the download-only policy.");
        }
    }
}
