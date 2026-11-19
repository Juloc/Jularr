namespace Jularr.Backend.Data.Enums.Acquisition
{
    // The kind of a free search across the indexers (Prowlarr's search types), plus games.
    public enum IndexerSearchType : byte
    {
        Search = 1,
        Tv = 2,
        Movie = 3,
        Music = 4,
        Book = 5,
        Game = 6
    }
}
