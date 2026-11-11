namespace Jularr.Integrations.Data.Nas
{
    // Storage that is out of reach: an offline or sleeping NAS, a missing volume or a refused session. It is never a deleted file.
    public sealed class NasMountException(string code, Exception? cause = null, TimeSpan? retryAfter = null) : Exception("The storage operation is unavailable.", cause)
    {
        // The storage refused Jularr's login or access: a storage problem, never the caller's own sign-in.
        public const string AccessDenied = "storage_access_denied";

        public string Code { get; } = code;

        // How long the storage stays closed, when its gate knows.
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}
