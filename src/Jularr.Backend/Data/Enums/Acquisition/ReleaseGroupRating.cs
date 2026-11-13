namespace Jularr.Backend.Data.Enums.Acquisition
{
    // How a release group of a media type is rated for every profile of that type: points for a good or a bad group, a blocked one is never taken automatically.
    public enum ReleaseGroupRating : byte
    {
        Good = 1,
        Bad = 2,
        Blocked = 3
    }

    // Which groups the list shows: not rated ones or one rating; none shows every group.
    public enum ReleaseGroupFilter : byte
    {
        NotRated = 1,
        Good = 2,
        Bad = 3,
        Blocked = 4
    }

    // The order of the list; rating first is the default.
    public enum ReleaseGroupSort : byte
    {
        Rating = 1,
        LastSeen = 2,
        SeenCount = 3,
        Name = 4
    }
}
