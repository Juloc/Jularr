using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Media.V1
{
    public sealed class FranchiseParametersV1 : BaseParameters
    {
        public FranchiseParametersV1(long franchiseId)
        {
            FranchiseId = franchiseId;
        }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Id))]
        public long FranchiseId { get; }
    }

    public sealed class FranchiseV1 : BaseResponse
    {
        public FranchiseV1(long id, string name)
        {
            Id = id;
            Name = name;
        }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Id))]
        public long Id { get; }

        [DataField(typeof(FranchiseFields), nameof(FranchiseFields.Name))]
        public string Name { get; }
    }
}
