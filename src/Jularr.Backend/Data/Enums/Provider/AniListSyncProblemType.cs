namespace Jularr.Backend.Data.Enums.Provider
{
    // Why a title's progress and its AniList entry stay apart instead of meeting at the next pass; derived, not a stored catalog.
    public enum AniListSyncProblemType : byte
    {
        SignInNeeded = 1,
        Unreachable = 2,
        SendingOff = 3,
        SendFailed = 4
    }
}
