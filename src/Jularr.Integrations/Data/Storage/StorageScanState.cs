namespace Jularr.Integrations.Data.Storage
{
    // An enumeration that skipped unreadable folders is incomplete.
    internal sealed class StorageScanState
    {
        public bool IsComplete { get; set; } = true;
    }
}
