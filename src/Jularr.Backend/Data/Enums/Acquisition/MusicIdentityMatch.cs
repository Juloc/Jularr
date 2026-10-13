namespace Jularr.Backend.Data.Enums.Acquisition
{
    internal enum MusicIdentityMatch : byte
    {
        Matches = 1,
        WrongTitle = 2,
        AmbiguousTitle = 3,
        NoDownload = 4
    }
}
