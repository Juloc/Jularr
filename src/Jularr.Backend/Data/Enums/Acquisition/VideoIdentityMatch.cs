namespace Jularr.Backend.Data.Enums.Acquisition
{
    internal enum VideoIdentityMatch : byte
    {
        Matches = 1,
        ContainsTarget = 2,
        WrongTitle = 3,
        WrongYear = 4,
        AmbiguousTitle = 5,
        WrongSeason = 6,
        WrongEpisode = 7,
        Unparseable = 8,
        NoDownload = 9
    }
}
