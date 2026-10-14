using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class WorkUnitGroupSaveData : BaseData
    {
        public WorkUnitGroupSaveData(long workId, string name, decimal? firstParentNumber, decimal firstNumber, decimal? lastParentNumber, decimal lastNumber)
        {
            WorkId = workId;
            Name = name;
            FirstParentNumber = firstParentNumber;
            FirstNumber = firstNumber;
            LastParentNumber = lastParentNumber;
            LastNumber = lastNumber;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkUnitGroupFields), nameof(WorkUnitGroupFields.Name))]
        public string Name { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? FirstParentNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber))]
        public decimal FirstNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? LastParentNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber))]
        public decimal LastNumber { get; }
    }

    internal sealed class WorkUnitGroupKeyData : BaseData
    {
        public WorkUnitGroupKeyData(long workId, long workUnitGroupId)
        {
            WorkId = workId;
            WorkUnitGroupId = workUnitGroupId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkUnitGroupFields), nameof(WorkUnitGroupFields.Id))]
        public long WorkUnitGroupId { get; }
    }

    internal sealed class WorkUnitGroupIdData : BaseData
    {
        public WorkUnitGroupIdData(long id)
        {
            Id = id;
        }

        [DataField(typeof(WorkUnitGroupFields), nameof(WorkUnitGroupFields.Id))]
        public long Id { get; }
    }
}
