using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    // One unit of a Work: an episode, a season, a volume or a track.
    public sealed class WorkUnitParametersV1 : BaseParameters
    {
        public WorkUnitParametersV1(long workId, long workUnitId)
        {
            WorkId = workId;
            WorkUnitId = workUnitId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitId))]
        public long WorkUnitId { get; }
    }

    public sealed class WorkUnitGroupParametersV1 : BaseParameters
    {
        public WorkUnitGroupParametersV1(long workId, long workUnitGroupId)
        {
            WorkId = workId;
            WorkUnitGroupId = workUnitGroupId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkUnitGroupFields), nameof(WorkUnitGroupFields.Id))]
        public long WorkUnitGroupId { get; }
    }

    // Both ends are included.
    public sealed class WorkUnitGroupV1 : BaseResponse
    {
        public WorkUnitGroupV1(long id, string name, decimal? firstParentNumber, decimal firstNumber, decimal? lastParentNumber, decimal lastNumber)
        {
            Id = id;
            Name = name;
            FirstParentNumber = firstParentNumber;
            FirstNumber = firstNumber;
            LastParentNumber = lastParentNumber;
            LastNumber = lastNumber;
        }

        [DataField(typeof(WorkUnitGroupFields), nameof(WorkUnitGroupFields.Id))]
        public long Id { get; }

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

    public sealed class CreateWorkUnitGroupDataV1 : BaseData
    {
        public CreateWorkUnitGroupDataV1(string name, decimal? firstParentNumber, decimal firstNumber, decimal? lastParentNumber, decimal lastNumber)
        {
            Name = name;
            FirstParentNumber = firstParentNumber;
            FirstNumber = firstNumber;
            LastParentNumber = lastParentNumber;
            LastNumber = lastNumber;
        }

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

    public sealed class CreateWorkUnitGroupResultV1 : BaseResponse
    {
        public CreateWorkUnitGroupResultV1(long id)
        {
            Id = id;
        }

        [DataField(typeof(WorkUnitGroupFields), nameof(WorkUnitGroupFields.Id))]
        public long Id { get; }
    }
}
