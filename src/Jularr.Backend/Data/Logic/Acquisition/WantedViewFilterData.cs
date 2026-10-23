using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class WantedViewFilterData : BaseData
    {
        public WantedViewFilterData(WantedViewType wantedViewTypeId)
        {
            WantedViewTypeId = wantedViewTypeId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.WantedViewTypeId))]
        public WantedViewType WantedViewTypeId { get; }
    }
}
