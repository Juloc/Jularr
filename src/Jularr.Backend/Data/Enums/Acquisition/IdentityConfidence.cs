namespace Jularr.Backend.Data.Enums.Acquisition
{
    // How sure the media type is that a release is the requested target.
    internal enum IdentityConfidence
    {
        // A trustworthy id or the exact unit matched.
        Exact,

        // Title and numbering agree without an id.
        Strong,

        // Plausible but not provable (for example absolute versus season numbering).
        Ambiguous,

        // The release is for something else.
        Conflict
    }
}
