namespace Jularr.Integrations.Data.Storage
{
    // A file or folder of a connection as a listing shows it.
    internal sealed record StorageEntry(string Name, bool IsDirectory, long Length, DateTime ModifiedAt);
}
