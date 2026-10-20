using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Media
{
    // The profile's progress of a Work, or of one of its units with everything below it.
    internal sealed class ProgressChangeData : BaseData
    {
        public ProgressChangeData(long profileId, long workId, long? workUnitId)
        {
            ProfileId = profileId;
            WorkId = workId;
            WorkUnitId = workUnitId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id), false)]
        public long? WorkUnitId { get; }
    }

    internal sealed class ProgressWorkData : BaseData
    {
        public ProgressWorkData(long id)
        {
            Id = id;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }
    }
}
