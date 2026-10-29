namespace Jularr.Backend.Data.Enums.Operation
{
    // What happens to the chosen downloads of the queue: wait, go on, read a waiting one again, take them out (and block their releases), or put them first in their client's queue.
    public enum DownloadBulkActionType : byte
    {
        Pause = 1,
        Resume = 2,
        Retry = 3,
        Remove = 4,
        RemoveAndBlock = 5,
        MoveToTop = 6
    }
}
