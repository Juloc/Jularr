namespace Jularr.Backend.Data.Enums.Operation
{
    // What runs, what waits for a person and what failed unseen; and what has finished.
    public enum ActivityTabType : byte
    {
        Queue = 1,
        History = 3
    }
}
