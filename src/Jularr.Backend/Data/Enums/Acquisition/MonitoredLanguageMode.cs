namespace Jularr.Backend.Data.Enums.Acquisition
{
    // Which languages a title nobody requested is wanted in.
    public enum MonitoredLanguageMode : byte
    {
        // The first language of every profile.
        AllProfiles = 1,

        // One chain the administrator sets.
        FixedChain = 2
    }
}
