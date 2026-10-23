using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Media
{
    public sealed class WatchlistData : BaseData
    {
        public WatchlistData(long profileId, long workId)
        {
            ProfileId = profileId;
            WorkId = workId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }
    }
}
