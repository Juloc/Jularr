using Jularr.Backend.Data.Common;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class ReadTorrentEngineParametersV1 : BaseParameters
    {
    }

    // What Jularr's own torrent engine did in this process, measured where it happens: the lock is checked now, the bytes are counted at the one writer.
    public sealed class TorrentEngineV1(bool isLockVerified, DateTime lockCheckedAt, long receivedBytes, long sentBytes, long sentPieceBytes, long checkedFrames,
        long refusedFrames, int connectedPeers, bool hasVerifiedTunnel) : BaseResponse
    {
        public bool IsLockVerified { get; } = isLockVerified;
        public DateTime LockCheckedAt { get; } = lockCheckedAt;

        // Verified file data received from peers.
        public long ReceivedBytes { get; } = receivedBytes;

        // Everything written to peers (handshakes, interest, requests) and the part of it that was file data.
        public long SentBytes { get; } = sentBytes;
        public long SentPieceBytes { get; } = sentPieceBytes;
        public long CheckedFrames { get; } = checkedFrames;
        public long RefusedFrames { get; } = refusedFrames;
        public int ConnectedPeers { get; } = connectedPeers;
        public bool HasVerifiedTunnel { get; } = hasVerifiedTunnel;
    }
}
