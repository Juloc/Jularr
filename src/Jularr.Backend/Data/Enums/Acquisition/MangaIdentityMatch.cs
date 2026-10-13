namespace Jularr.Backend.Data.Enums.Acquisition
{
    internal enum MangaIdentityMatch : byte
    {
        Matches = 1,
        ContainsTarget = 2,
        WrongTitle = 3,
        AmbiguousTitle = 4,
        WrongVolume = 5,
        NoDownload = 6
    }
}
