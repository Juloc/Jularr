namespace Jularr.Backend.Data.Enums.Media
{
    // Ranked provider lists; a type only offers the lists its provider truly has.
    public enum DiscoverFeedType : byte
    {
        Trending = 1,
        Top = 2,
        New = 3,
        Upcoming = 4,
        // AniList's lists: the popular titles of this and the next anime season, of all time and of Korea, and the hundred best rated.
        Season = 5,
        NextSeason = 6,
        Popular = 7,
        Manhwa = 8,
        Top100 = 9
    }
}
