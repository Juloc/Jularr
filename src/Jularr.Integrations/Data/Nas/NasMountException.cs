namespace Jularr.Integrations.Data.Nas
{
    // Storage that is out of reach: an offline or sleeping NAS, a missing volume or a refused session. It is never a deleted file.
    public sealed class NasMountException(string code) : Exception("The storage operation is unavailable.")
    {
        public string Code { get; } = code;
    }
}
