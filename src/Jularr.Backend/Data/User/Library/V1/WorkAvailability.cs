using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Library.V1
{
    public sealed class ReadWorkAvailabilityStatesParametersV1 : BaseParameters
    {
        public ReadWorkAvailabilityStatesParametersV1(long[] workIds)
        {
            WorkIds = workIds;
        }

        [DataField(typeof(WorkRelationFields), nameof(WorkRelationFields.WorkIds))]
        public long[] WorkIds { get; }
    }

    public sealed class WorkAvailabilityStateV1 : BaseResponse
    {
        public WorkAvailabilityStateV1(long id, MediaAvailabilityState availabilityStateId, int availableUnits, int monitoredUnits)
        {
            Id = id;
            AvailabilityStateId = availabilityStateId;
            AvailableUnits = availableUnits;
            MonitoredUnits = monitoredUnits;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.AvailabilityStateId))]
        public MediaAvailabilityState AvailabilityStateId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.AvailableUnits))]
        public int AvailableUnits { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MonitoredUnits))]
        public int MonitoredUnits { get; }
    }
}
