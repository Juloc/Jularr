namespace Jularr.Integrations.Data.Storage
{
    internal sealed class StorageFile
    {
        public StorageFile(string relativePath, long sizeBytes, DateTime modifiedAt)
        {
            RelativePath = relativePath;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
        }

        public string RelativePath { get; }
        public long SizeBytes { get; }
        public DateTime ModifiedAt { get; }
    }
}
