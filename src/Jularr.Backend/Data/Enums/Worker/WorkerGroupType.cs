namespace Jularr.Backend.Data.Enums.Worker
{
    // The hosted workers, and the sampler that is no worker.
    public enum WorkerGroupType : byte
    {
        Operation = 1,
        Acquisition = 2,
        Usenet = 3,
        Media = 4,
        Storage = 5,
        Backup = 6,
        Maintenance = 7,
        Trickplay = 8,
        Subtitle = 9,
        Recognition = 10,
        Catalog = 11,
        Sampler = 12,
        Download = 13,
        Provider = 14,
        Derivation = 15,
        Health = 16
    }
}
