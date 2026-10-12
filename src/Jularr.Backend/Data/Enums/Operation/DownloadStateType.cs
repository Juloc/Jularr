namespace Jularr.Backend.Data.Enums.Operation
{
    // What the download client is doing with a running download.
    public enum DownloadStateType : byte
    {
        Queued = 1,
        Downloading = 2,
        Paused = 3,
        PostProcessing = 4
    }
}
