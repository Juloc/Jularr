namespace Jularr.Integrations.Data.Nas
{
    internal enum NasMountAction : byte
    {
        Status = 1,
        Connect = 2,
        Disconnect = 3
    }

    internal sealed class NasMountRequest
    {
        public NasMountRequest(NasMountAction action, long storageMountId, long version, string protocol, string host, string sharePath, string protocolVersion, string? username, string? domain, string? password, bool isReadOnly)
        {
            Action = action;
            StorageMountId = storageMountId;
            Version = version;
            Protocol = protocol;
            Host = host;
            SharePath = sharePath;
            ProtocolVersion = protocolVersion;
            Username = username;
            Domain = domain;
            Password = password;
            IsReadOnly = isReadOnly;
        }

        public NasMountAction Action { get; }
        public long StorageMountId { get; }
        public long Version { get; }
        public string Protocol { get; }
        public string Host { get; }
        public string SharePath { get; }
        public string ProtocolVersion { get; }
        public string? Username { get; }
        public string? Domain { get; }
        public string? Password { get; }
        public bool IsReadOnly { get; }
    }

    internal sealed class NasMountResponse
    {
        public NasMountResponse(bool isMounted, long mountedVersion, string? errorCode = null)
        {
            IsMounted = isMounted;
            MountedVersion = mountedVersion;
            ErrorCode = errorCode;
        }

        public bool IsMounted { get; }
        public long MountedVersion { get; }
        public string? ErrorCode { get; }
    }

    public sealed class NasMountException(string code) : Exception("The storage operation is unavailable.")
    {
        public string Code { get; } = code;
    }
}
