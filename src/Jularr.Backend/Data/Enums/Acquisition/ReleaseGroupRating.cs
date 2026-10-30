namespace Jularr.Backend.Data.Enums.Acquisition
{
    // How a release group of a media type is rated for every profile of that type: points for a good or a bad group, a blocked one is never taken automatically.
    public enum ReleaseGroupRating : byte
    {
        Good = 1,
        Bad = 2,
        Blocked = 3
    }
}
