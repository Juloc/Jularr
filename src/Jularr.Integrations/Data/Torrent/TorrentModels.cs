namespace Jularr.Integrations.Data.Torrent
{
    // Opens a TCP connection for the engine: through the bound VPN tunnel in Jularr, directly only in tests.
    internal delegate Task<Stream> TorrentConnector(string host, int port, CancellationToken ct);

    internal enum TorrentPassState : byte
    {
        Downloading = 1,
        Completed = 2,
        Failed = 3
    }

    // One pass of a job: its folder holds job.torrent or job.magnet, the fetched metadata, the verified pieces and the data.
    internal sealed class TorrentPassRequest(string jobDirectory, TorrentConnector connect, HttpClient trackers, DateTimeOffset deadline, int maximumPeers = 50,
        Func<long, CancellationToken, ValueTask>? throttle = null)
    {
        // The downloader's speed limit: every block waits for its turn before it is taken.
        public Func<long, CancellationToken, ValueTask>? Throttle { get; } = throttle;

        public string JobDirectory { get; } = jobDirectory;
        public TorrentConnector Connect { get; } = connect;
        public HttpClient Trackers { get; } = trackers;
        public DateTimeOffset Deadline { get; } = deadline;
        public int MaximumPeers { get; } = maximumPeers;
    }

    internal sealed class TorrentPassResult(TorrentPassState state, int progressPercent, long sizeBytes, long remainingBytes, long receivedBytes, int peers,
        bool progressed, string? reasonKey = null, string? outputPath = null)
    {
        public TorrentPassState State { get; } = state;
        public int ProgressPercent { get; } = progressPercent;
        public long SizeBytes { get; } = sizeBytes;
        public long RemainingBytes { get; } = remainingBytes;

        // Verified file data received from peers in this pass.
        public long ReceivedBytes { get; } = receivedBytes;
        public int Peers { get; } = peers;
        public bool Progressed { get; } = progressed;
        public string? ReasonKey { get; } = reasonKey;
        public string? OutputPath { get; } = outputPath;
    }
}
