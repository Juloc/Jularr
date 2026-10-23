namespace Jularr.Backend.Data.Enums.Library
{
    // What a unit of a title is right now, as the management page shows it.
    public enum WorkUnitStatus : byte
    {
        Present = 1,
        Missing = 2,
        Downloading = 3,
        Unaired = 4,
        NotMonitored = 5
    }
}
