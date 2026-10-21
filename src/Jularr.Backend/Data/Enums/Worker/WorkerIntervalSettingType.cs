namespace Jularr.Backend.Data.Enums.Worker
{
    // The Owner setting that decides how often a job runs; none for a fixed interval.
    public enum WorkerIntervalSettingType : byte
    {
        None = 0,
        BackupHours = 1,
        ProviderSyncMinutes = 2,
        WantedSearchMinutes = 3
    }
}
