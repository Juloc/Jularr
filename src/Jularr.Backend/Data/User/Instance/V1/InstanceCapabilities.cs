using Jularr.Backend.Data.Common;

namespace Jularr.Backend.Data.User.Instance.V1
{
    public sealed class ReadInstanceCapabilitiesParametersV1 : BaseParameters
    {
    }

    public sealed class InstanceCapabilitiesDetailsV1 : BaseResponse
    {
        public InstanceCapabilitiesDetailsV1(bool canReadMedia, bool canManageMedia, bool canUpdateWatchlist, bool canRequest, bool canManageRequests, bool canManageWanted)
        {
            CanReadMedia = canReadMedia;
            CanManageMedia = canManageMedia;
            CanUpdateWatchlist = canUpdateWatchlist;
            CanRequest = canRequest;
            CanManageRequests = canManageRequests;
            CanManageWanted = canManageWanted;
        }

        public bool CanReadMedia { get; }
        public bool CanManageMedia { get; }
        public bool CanUpdateWatchlist { get; }
        public bool CanRequest { get; }
        public bool CanManageRequests { get; }
        public bool CanManageWanted { get; }
    }
}
