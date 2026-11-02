namespace Jularr.Backend.Data.Enums.Provider
{
    // How a profile's progress in Jularr stands against its AniList entry for one title; derived, not a stored catalog.
    public enum AniListSyncStateType : byte
    {
        Synchronized = 1,
        LocalAhead = 2,
        AniListAhead = 3
    }
}
