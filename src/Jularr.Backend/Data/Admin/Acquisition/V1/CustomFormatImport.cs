using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class ImportCustomFormatsDataV1 : BaseData
    {
        public ImportCustomFormatsDataV1(TakeoverSourceType takeoverSourceTypeId, string json)
        {
            TakeoverSourceTypeId = takeoverSourceTypeId;
            Json = json;
        }

        [DataField(typeof(TakeoverFields), nameof(TakeoverFields.TakeoverSourceTypeId))]
        public TakeoverSourceType TakeoverSourceTypeId { get; }

        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.ImportJson))]
        public string Json { get; }
    }
}
