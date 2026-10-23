namespace Jularr.Backend.Data.Enums.Notification
{
    public enum NotificationType : byte
    {
        RequestApproved = 1,
        RequestRejected = 2,
        MediaAvailable = 3,
        DownloadFailed = 4,
        StorageOffline = 5,
        // A finished download waits for a person; its subject holds the title and the reason, apart by NotificationSubject.
        DownloadWaiting = 6
    }
}
