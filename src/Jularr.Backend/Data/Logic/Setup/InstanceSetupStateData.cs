using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Setup;

namespace Jularr.Backend.Data.Logic.Setup
{
    public sealed class InstanceSetupStateData : BaseData
    {
        public InstanceSetupStateData(long id, DateTime? ownerCreatedAt)
        {
            Id = id;
            OwnerCreatedAt = ownerCreatedAt;
        }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.Id))]
        public long Id { get; }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.OwnerCreatedAt))]
        public DateTime? OwnerCreatedAt { get; }
    }
}
