namespace Jularr.Backend.Data.Enums.Media
{
    // What a row of an imported list is, before anything happens.
    public enum MediaImportStatusType : byte
    {
        New = 1,
        InLibrary = 2,
        Duplicate = 3,
        Open = 4,
        Invalid = 5
    }

    // What an import does with a title: only add it, also monitor it, or monitor it and search at once.
    public enum MediaImportActionType : byte
    {
        AddOnly = 1,
        Monitor = 2,
        MonitorAndSearch = 3
    }

    // What became of a row.
    public enum MediaImportOutcomeType : byte
    {
        Added = 1,
        AddedAndMonitored = 2,
        Monitored = 3,
        Unchanged = 4,
        Skipped = 5,
        NotFound = 6,
        Unavailable = 7,
        Excluded = 8,
        Deferred = 9,
        Failed = 10
    }
}
