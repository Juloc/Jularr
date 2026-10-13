namespace Jularr.Backend.Data.Enums.Acquisition
{
    internal enum GameIdentityMatch : byte
    {
        Matches = 1,
        WrongTitle = 2,
        AmbiguousTitle = 3,
        WrongPlatform = 4,
        NoDownload = 5
    }
}
