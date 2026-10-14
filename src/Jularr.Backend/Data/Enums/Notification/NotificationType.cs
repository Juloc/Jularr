namespace Jularr.Backend.Data.Enums.Notification
{
    public enum NotificationType : byte
    {
        RequestApproved = 1,
        RequestRejected = 2,
        MediaAvailable = 3,
        DownloadFailed = 4,
        StorageOffline = 5
    }
}
