namespace Jularr.Backend.Data.Enums.Worker
{
    // What the Jobs page shows for a job right now, most telling first.
    public enum WorkerJobStateType : byte
    {
        Running = 1,
        Paused = 2,
        WaitingForUse = 3,
        Failed = 4,
        Off = 5,
        Idle = 6,
        Pending = 7
    }
}
