namespace Jularr.Backend.Data.Enums.Instance
{
    // Where a log message comes from: what happened to the media, a failed background job, or the application's own warnings and errors.
    public enum LogSourceType : byte
    {
        Activity = 1,
        Job = 2,
        Application = 3
    }
}
