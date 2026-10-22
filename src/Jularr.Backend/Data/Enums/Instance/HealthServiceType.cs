namespace Jularr.Backend.Data.Enums.Instance
{
    // What a checked service is, in the order the Dashboard lists them.
    public enum HealthServiceType : byte
    {
        Database = 1,
        Storage = 2,
        Provider = 3,
        MediaServer = 4,
        DownloadClient = 5,
        Indexer = 6
    }
}
