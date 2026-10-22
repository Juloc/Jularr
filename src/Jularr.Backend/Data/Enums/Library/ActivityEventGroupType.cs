namespace Jularr.Backend.Data.Enums.Library
{
    // The filter chips of the history: what was fetched, what arrived (imports, upgrades, assignments by hand, subtitles), what failed and what happened to files and titles.
    public enum ActivityEventGroupType : byte
    {
        All = 0,
        Grabbed = 1,
        Arrived = 2,
        Failed = 3,
        Files = 4
    }
}
