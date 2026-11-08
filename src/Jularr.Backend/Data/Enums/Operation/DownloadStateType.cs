namespace Jularr.Backend.Data.Enums.Operation
{
    // What the download client is doing with a running download.
    public enum DownloadStateType : byte
    {
        Queued = 1,
        Downloading = 2,
        Paused = 3,
        PostProcessing = 4,
        Completed = 5,
        Verifying = 6,
        Repairing = 7,
        Extracting = 8,
        Moving = 9,
        Resolving = 10
    }

    public static class DownloadStates
    {
        public static bool IsHistory(DownloadStateType? state) => state is DownloadStateType.PostProcessing or DownloadStateType.Completed
            or DownloadStateType.Verifying or DownloadStateType.Repairing or DownloadStateType.Extracting or DownloadStateType.Moving;
    }
}
