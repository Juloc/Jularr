namespace Jularr.Backend.Data.Enums.Media
{
    // How a ranked list is ordered instead of its own order: AniList's orders, of which a whole local catalog has those its data allows.
    public enum DiscoverSort : byte
    {
        Trending = 1,
        Popularity = 2,
        Score = 3,
        Favourites = 4,
        Added = 5,
        Released = 6,
        Title = 7
    }
}
