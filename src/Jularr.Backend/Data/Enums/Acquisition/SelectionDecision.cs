namespace Jularr.Backend.Data.Enums.Acquisition
{
    internal enum SelectionDecision
    {
        // Acceptable now.
        Eligible,

        // Only an owner may take it (ambiguous identity).
        ManualReview,

        Rejected
    }
}
