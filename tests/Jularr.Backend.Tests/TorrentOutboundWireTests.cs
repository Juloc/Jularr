using System.Buffers.Binary;
using System.Text;
using Jularr.Integrations.Logic.Torrent;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class TorrentOutboundWireTests
    {
        [TestMethod]
        public async Task PlainHandshake_IsAllowedWithoutExtensionNegotiation()
        {
            var handshake = new byte[68];
            handshake[0] = 19;
            Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);
            await using var outbound = new MemoryStream();

            await TorrentOutboundWire.SendHandshakeAsync(outbound, handshake, CancellationToken.None);

            CollectionAssert.AreEqual(handshake, outbound.ToArray());
        }

        [TestMethod]
        public async Task ExtensionHandshake_IsBlockedBeforeWriting()
        {
            var handshake = new byte[68];
            handshake[0] = 19;
            Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);
            handshake[25] = 0x10; // BEP10 extension protocol.
            await using var outbound = new MemoryStream();

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => TorrentOutboundWire.SendHandshakeAsync(outbound, handshake, CancellationToken.None));

            Assert.AreEqual(0L, outbound.Length);
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(2)]
        [DataRow(3)]
        [DataRow(15)]
        public async Task ControlMessage_IsAllowed(int messageId)
        {
            var frame = MakeFrame((byte)messageId);
            await using var outbound = new MemoryStream();

            await TorrentOutboundWire.SendMessageAsync(outbound, frame, CancellationToken.None);

            CollectionAssert.AreEqual(frame, outbound.ToArray());
        }

        [TestMethod]
        [DataRow(6)]
        [DataRow(8)]
        [DataRow(16)]
        public async Task BoundedRequestCancelOrReject_IsAllowed(int messageId)
        {
            var payload = new byte[12];
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), 16 * 1024);
            var frame = MakeFrame((byte)messageId, payload);
            await using var outbound = new MemoryStream();

            await TorrentOutboundWire.SendMessageAsync(outbound, frame, CancellationToken.None);

            CollectionAssert.AreEqual(frame, outbound.ToArray());
        }

        [TestMethod]
        [DataRow(1)] // Unchoke
        [DataRow(4)] // Have
        [DataRow(5)] // Bitfield
        [DataRow(7)] // Piece
        [DataRow(14)] // Have all
        [DataRow(17)] // Allowed fast
        [DataRow(20)] // Extension
        [DataRow(255)] // Unknown
        public async Task UploadAdvertisingAndUnrecognizedMessages_AreBlockedBeforeWriting(int messageId)
        {
            await AssertBlockedAsync(MakeFrame((byte)messageId, new byte[16]));
        }

        [TestMethod]
        public async Task ExplicitPieceWithFileBytes_IsBlockedBeforeWriting()
        {
            var frame = MakeFrame(7, Enumerable.Repeat((byte)0xAB, 16 * 1024).ToArray());
            await AssertBlockedAsync(frame);
        }

        [TestMethod]
        public async Task KeepAlive_IsAllowed()
        {
            await using var outbound = new MemoryStream();
            var frame = new byte[4];

            await TorrentOutboundWire.SendMessageAsync(outbound, frame, CancellationToken.None);

            CollectionAssert.AreEqual(frame, outbound.ToArray());
        }

        [TestMethod]
        public async Task MalformedFrames_FailClosedBeforeWriting()
        {
            await AssertBlockedAsync([0, 0, 0]);
            await AssertBlockedAsync([0, 0, 0, 17, 6]);
            await AssertBlockedAsync([255, 255, 255, 255]);
            await AssertBlockedAsync(MakeFrame(6, new byte[12])); // No zero-length requests.
            var oversized = new byte[12];
            BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(8), 16 * 1024 + 1);
            await AssertBlockedAsync(MakeFrame(6, oversized));
        }

        private static byte[] MakeFrame(byte messageId, byte[]? messageBody = null)
        {
            messageBody ??= [];
            var frame = new byte[5 + messageBody.Length];
            BinaryPrimitives.WriteInt32BigEndian(frame, 1 + messageBody.Length);
            frame[4] = messageId;
            messageBody.CopyTo(frame, 5);
            return frame;
        }

        private static async Task AssertBlockedAsync(byte[] frame)
        {
            await using var outbound = new MemoryStream();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => TorrentOutboundWire.SendMessageAsync(outbound, frame, CancellationToken.None));
            Assert.AreEqual(0L, outbound.Length);
        }
    }
}
