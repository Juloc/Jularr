namespace Jularr.Backend.Data.Enums.Media
{
    // Ranked provider lists; a type only offers the lists its provider truly has.
    public enum DiscoverFeedType : byte
    {
        Trending = 1,
        Top = 2,
        New = 3,
        Upcoming = 4
    }
}
