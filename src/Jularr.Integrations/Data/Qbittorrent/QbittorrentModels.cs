namespace Jularr.Integrations.Data.Qbittorrent
{
    internal sealed class QbittorrentConnection
    {
        public QbittorrentConnection(string baseUrl, string? userName, string? password)
        {
            BaseUrl = baseUrl;
            UserName = userName;
            Password = password;
        }

        public string BaseUrl { get; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string? UserName { get; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string? Password { get; }
    }

    // The version proves the client answers; the torrent list proves the login works.
    internal sealed class QbittorrentConnectionTestResult
    {
        public QbittorrentConnectionTestResult(bool isAvailable, string? version)
        {
            IsAvailable = isAvailable;
            Version = version;
        }

        public bool IsAvailable { get; }
        public string? Version { get; }
    }

    internal sealed class QbittorrentGrabRequest
    {
        public QbittorrentGrabRequest(Uri torrentUrl, string savePath, string? title)
        {
            TorrentUrl = torrentUrl;
            SavePath = savePath;
            Title = title;
        }

        public Uri TorrentUrl { get; }

        // The folder the client writes the finished file into.
        public string SavePath { get; }

        public string? Title { get; }
    }

    internal sealed class QbittorrentGrabResult
    {
        public QbittorrentGrabResult(bool success, string? hash, string? reasonKey = null)
        {
            Success = success;
            Hash = hash;
            ReasonKey = reasonKey;
        }

        public bool Success { get; }

        // The identity of the torrent in the client; the source usually knows it beforehand.
        public string? Hash { get; }

        public string? ReasonKey { get; }
    }

    // The state of one torrent in the client.
    internal sealed class QbittorrentTorrent
    {
        public QbittorrentTorrent(string hash, string name, string state, double progress, long sizeBytes, long downloadedBytes,
            long downloadSpeed, int seeds, int peers, string? contentPath, string? savePath, long? addedAt = null, string? errorReason = null, IReadOnlyList<string>? tags = null)
        {
            Hash = hash;
            Name = name;
            State = state;
            Progress = progress;
            SizeBytes = sizeBytes;
            DownloadedBytes = downloadedBytes;
            DownloadSpeed = downloadSpeed;
            Seeds = seeds;
            Peers = peers;
            ContentPath = contentPath;
            SavePath = savePath;
            AddedAt = addedAt;
            ErrorReason = errorReason;
            Tags = tags ?? [];
        }

        public string Hash { get; }
        public string Name { get; }

        // downloading, stalled, paused, error, missingFiles, checking, moving, uploading, queued, forcedUP, unknown.
        public string State { get; }

        // The part of the file that is there, from 0 to 1.
        public double Progress { get; }

        public long SizeBytes { get; }
        public long DownloadedBytes { get; }
        public long DownloadSpeed { get; }
        public int Seeds { get; }
        public int Peers { get; }

        // The file or folder the client wrote; the import reads it from there.
        public string? ContentPath { get; }

        public string? SavePath { get; }

        // When the client took the torrent; the newest one is the one just added.
        public long? AddedAt { get; }

        // The client's own reason, when it has one.
        public string? ErrorReason { get; }
        public IReadOnlyList<string> Tags { get; }

        public bool IsCompleted => Progress >= 1 && !IsFailed && State is not ("checkingDL" or "checkingUP" or "moving");

        public bool IsFailed => State is "error" or "missingFiles";

        public bool IsPaused => State is "paused" or "pausedDL" or "pausedUP" or "stoppedDL" or "stoppedUP";

        public bool IsActive => State is "downloading" or "stalledDL" or "metaDL" or "forcedMetaDL" or "checkingDL" or "queuedDL" or "allocating" or "forcedDL";
    }

    internal sealed class QbittorrentSnapshot
    {
        public QbittorrentSnapshot(IReadOnlyList<QbittorrentTorrent> torrents)
        {
            Torrents = torrents;
        }

        public IReadOnlyList<QbittorrentTorrent> Torrents { get; }
    }

    // Messages never contain the password.
    internal sealed class QbittorrentException(string message, Exception? innerException = null) : Exception(message, innerException);
}
