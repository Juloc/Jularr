namespace Jularr.Backend.Data.Enums.Acquisition
{
    // Applications Jularr takes over once; never run alongside.
    public enum TakeoverSourceType : byte
    {
        Prowlarr = 1,
        Sonarr = 2,
        Radarr = 3,
        Lidarr = 4,
        Readarr = 5
    }

    public enum TakeoverItemType : byte
    {
        Indexer = 1,
        Client = 2,
        Movie = 3,
        Series = 4,
        Album = 5,
        Book = 6
    }
}
