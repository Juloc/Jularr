using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class QualityDefinitionsWriteData : BaseData
    {
        public QualityDefinitionsWriteData(MediaType mediaTypeId, string rows)
        {
            MediaTypeId = mediaTypeId;
            Rows = rows;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Rows))]
        public string Rows { get; }
    }
}
