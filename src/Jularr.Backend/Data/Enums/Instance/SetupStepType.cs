namespace Jularr.Backend.Data.Enums.Instance
{
    // Persisted wizard identifiers; navigation defines their display order.
    public enum SetupStepType : byte
    {
        Instance = 1,
        Storage = 2,
        Indexers = 3,
        Downloads = 4,
        Providers = 5,
        Profiles = 6,
        Media = 7,
        Finish = 8,
        DownloadStorage = 9
    }
}
