using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class ImportCustomFormatsDataV1 : BaseData
    {
        public ImportCustomFormatsDataV1(MediaType mediaTypeId, TakeoverSourceType takeoverSourceTypeId, string json)
        {
            MediaTypeId = mediaTypeId;
            TakeoverSourceTypeId = takeoverSourceTypeId;
            Json = json;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(TakeoverFields), nameof(TakeoverFields.TakeoverSourceTypeId))]
        public TakeoverSourceType TakeoverSourceTypeId { get; }

        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.ImportJson))]
        public string Json { get; }
    }
}
