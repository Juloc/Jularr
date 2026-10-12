using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class AcquisitionRequestFilterData : BaseData
    {
        public AcquisitionRequestFilterData(RequestTabType requestTabTypeId)
        {
            RequestTabTypeId = requestTabTypeId;
        }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.RequestTabTypeId))]
        public RequestTabType RequestTabTypeId { get; }
    }
}
