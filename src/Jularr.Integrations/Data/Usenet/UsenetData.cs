namespace Jularr.Integrations.Data.Usenet
{
    internal sealed class UsenetStorageBudgetException : Exception
    {
        public UsenetStorageBudgetException() : base("The configured Usenet job storage limit was exceeded.")
        {
        }
    }

    internal enum UsenetFailure : byte
    {
        Unavailable,
        Unauthorized,
        ConnectionLimit,
        ArticleMissing,
        InvalidResponse
    }

    internal sealed class UsenetException : Exception
    {
        public UsenetException(UsenetFailure failure, string message, Exception? innerException = null) : base(message, innerException)
        {
            Failure = failure;
        }

        public UsenetFailure Failure { get; }
    }

    // One news server; the password never leaves this process except to that server.
    internal sealed class UsenetServer
    {
        public UsenetServer(string host, int port, bool useTls, string? username, string? password, int connections)
        {
            Host = host;
            Port = port;
            UseTls = useTls;
            Username = username;
            Password = password;
            Connections = connections;
        }

        public string Host { get; }
        public int Port { get; }
        public bool UseTls { get; }
        public string? Username { get; }
        public string? Password { get; }
        public int Connections { get; }
    }

    internal sealed class NzbFile
    {
        public NzbFile(string fileName, IReadOnlyList<NzbSegment> segments)
        {
            FileName = fileName;
            Segments = segments;
        }

        // A safe local file name from the subject.
        public string FileName { get; }
        public IReadOnlyList<NzbSegment> Segments { get; }
        public long Bytes => Segments.Sum(segment => segment.Bytes);
    }

    internal sealed class NzbSegment
    {
        public NzbSegment(int number, long bytes, string messageId)
        {
            Number = number;
            Bytes = bytes;
            MessageId = messageId;
        }

        public int Number { get; }
        public long Bytes { get; }
        public string MessageId { get; }
    }

    // One decoded yEnc article: where its bytes belong in the file and whether they are intact.
    internal sealed class YencPart
    {
        public YencPart(long offset, byte[] data, bool isIntact)
        {
            Offset = offset;
            Data = data;
            IsIntact = isIntact;
        }

        public long Offset { get; }
        public byte[] Data { get; }
        public bool IsIntact { get; }
    }

    // What a download pass achieved; damaged and missing articles need repair.
    internal sealed class UsenetProgress
    {
        public UsenetProgress(IReadOnlyCollection<string> done, IReadOnlyCollection<string> damaged, long downloadedBytes, int sessions = 0)
        {
            Done = done;
            Damaged = damaged;
            DownloadedBytes = downloadedBytes;
            Sessions = sessions;
        }

        // How many connections the pass held open to the server.
        public int Sessions { get; }

        public IReadOnlyCollection<string> Done { get; }
        public IReadOnlyCollection<string> Damaged { get; }
        public long DownloadedBytes { get; }
    }
}
