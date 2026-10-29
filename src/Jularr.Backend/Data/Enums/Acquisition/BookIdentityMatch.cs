namespace Jularr.Backend.Data.Enums.Acquisition
{
    internal enum BookIdentityMatch : byte
    {
        Matches = 1,
        WrongTitle = 2,
        AmbiguousTitle = 3,
        NoDownload = 4,

        // The title matches, but the release is not an e-book (an audiobook, a manga, a magazine).
        WrongKind = 5
    }
}
