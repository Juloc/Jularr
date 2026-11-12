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

    // A video profile's choice about x265: alike, liked (points) or avoided (points off).
    public enum CodecPreference : byte
    {
        Any = 0,
        PreferHevc = 1,
        AvoidHevc = 2
    }

    // A video profile's choice between two releases of the same quality and points: alike, the smaller or the larger file.
    public enum SizePreference : byte
    {
        Any = 0,
        Smaller = 1,
        Larger = 2
    }
}
