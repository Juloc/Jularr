namespace Jularr.Backend.Data.Enums.Library
{
    // What happened to a Work's media (ActivityEventTypes).
    public enum ActivityEventType : byte
    {
        Grabbed = 1,
        DownloadFailed = 2,
        Imported = 3,
        Upgraded = 4,
        FileRenamed = 5,
        FileDeleted = 6,
        ManualImport = 7,
        Blocked = 8,
        SubtitleDownloaded = 9
    }
}
