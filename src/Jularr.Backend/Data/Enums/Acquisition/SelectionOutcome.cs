namespace Jularr.Backend.Data.Enums.Acquisition
{
    // What a whole search came to, which drives how Wanted retries.
    internal enum SelectionOutcome
    {
        NoCandidates,
        IdentityInvalid,
        ProfileRejected,
        ManualReviewOnly,
        Usable
    }
}
