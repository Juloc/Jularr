namespace Jularr.Backend.Data.Enums.Instance
{
    // Persisted wizard steps in the order of the flow; the wizard resumes at the furthest one reached.
    public enum SetupStepType : byte
    {
        Begin = 1,
        Storage = 2,
        Takeover = 3,
        Modules = 4,
        Profiles = 5,
        Libraries = 6,
        Requests = 7,
        DownloadStorage = 8,
        Downloads = 9,
        Indexers = 10,
        Providers = 11,
        Overview = 12
    }

    // How the instance starts: empty, or filled from existing Sonarr, Radarr, Readarr and Lidarr systems.
    public enum SetupStartType : byte
    {
        New = 1,
        Takeover = 2
    }
}
