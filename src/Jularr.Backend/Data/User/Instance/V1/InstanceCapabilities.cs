using Jularr.Backend.Data.Common;

namespace Jularr.Backend.Data.User.Instance.V1
{
    public sealed class ReadInstanceCapabilitiesParametersV1 : BaseParameters
    {
    }

    public sealed class InstanceCapabilitiesDetailsV1 : BaseResponse
    {
        public InstanceCapabilitiesDetailsV1(bool canReadMedia, bool canManageMedia, bool canUpdateWatchlist, bool canRequest, bool canManageRequests, bool canManageWanted, bool canAcquire, bool canPlay, bool canRead,
            bool canManageProfiles, bool canPlayGames, bool canNotify, bool canCollect, bool canSeeCalendar, bool canSeeStatistics)
        {
            CanSeeStatistics = canSeeStatistics;
            CanSeeCalendar = canSeeCalendar;
            CanCollect = canCollect;
            CanNotify = canNotify;
            CanPlayGames = canPlayGames;
            CanRead = canRead;
            CanManageProfiles = canManageProfiles;
            CanReadMedia = canReadMedia;
            CanManageMedia = canManageMedia;
            CanUpdateWatchlist = canUpdateWatchlist;
            CanRequest = canRequest;
            CanManageRequests = canManageRequests;
            CanManageWanted = canManageWanted;
            CanAcquire = canAcquire;
            CanPlay = canPlay;
        }

        public bool CanReadMedia { get; }
        public bool CanManageMedia { get; }
        public bool CanUpdateWatchlist { get; }
        public bool CanRequest { get; }
        public bool CanManageRequests { get; }
        public bool CanManageWanted { get; }
        public bool CanAcquire { get; }
        public bool CanPlay { get; }

        // Book processing and the reader are switched on.
        public bool CanRead { get; }

        // Several profiles with picker and PIN; otherwise each account uses its main profile.
        public bool CanManageProfiles { get; }

        // Retro games play in the browser.
        public bool CanPlayGames { get; }

        // The profile has an inbox.
        public bool CanNotify { get; }

        // The profile keeps its own collections.
        public bool CanCollect { get; }

        // Upcoming releases of the profile's Works.
        public bool CanSeeCalendar { get; }

        // The profile's own history in numbers.
        public bool CanSeeStatistics { get; }
    }
}
