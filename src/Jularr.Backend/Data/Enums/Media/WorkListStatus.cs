namespace Jularr.Backend.Data.Enums.Media
{
    // Where a title stands in the administrator's library list: filed, still to get, on its way or not wanted.
    public enum WorkListStatus : byte
    {
        Available = 1,
        Missing = 2,
        Downloading = 3,
        Unmonitored = 4
    }
}
