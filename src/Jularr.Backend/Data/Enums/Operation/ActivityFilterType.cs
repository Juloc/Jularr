namespace Jularr.Backend.Data.Enums.Operation
{
    // The one row of the queue page: everything in the queue, what runs (paused downloads too), what waits for a person, what unpacks or imports, what failed unseen, and what has finished.
    // The values are those of the view OperationQueueStates.
    public enum ActivityFilterType : byte
    {
        All = 1,
        Active = 2,
        History = 3,
        Waiting = 4,
        Import = 5,
        Errors = 6
    }
}
