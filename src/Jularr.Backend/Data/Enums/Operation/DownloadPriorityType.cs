namespace Jularr.Backend.Data.Enums.Operation
{
    // How soon a client takes a download: SABnzbd's four levels; a forced download keeps running when its client or the whole queue is paused.
    public enum DownloadPriorityType : byte
    {
        Low = 1,
        Normal = 2,
        High = 3,
        Force = 4
    }
}
