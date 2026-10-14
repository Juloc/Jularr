namespace Jularr.Integrations.Data.Storage
{
    // Readable or why not, with volume space.
    internal sealed class StorageProbe(bool isReadable, string? code, long? freeBytes, long? totalBytes)
    {
        public bool IsReadable { get; } = isReadable;
        public string? Code { get; } = code;
        public long? FreeBytes { get; } = freeBytes;
        public long? TotalBytes { get; } = totalBytes;
    }
}
