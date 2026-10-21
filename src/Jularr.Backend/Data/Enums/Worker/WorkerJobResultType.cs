namespace Jularr.Backend.Data.Enums.Worker
{
    // How a job's last pass ended; a skipped pass found its module switched off.
    public enum WorkerJobResultType : byte
    {
        Succeeded = 1,
        Failed = 2,
        Skipped = 3
    }
}
