namespace Jularr.Backend.Service.Core
{
    // The endpoint is an administrator-controlled deployment setting, not a profile URL.
    public sealed class LocalTranslationOptions
    {
        public string? Endpoint { get; set; }
    }

    internal sealed class LocalTranslationGate
    {
        public SemaphoreSlim HeavyWork { get; } = new(1, 1);
    }
}
