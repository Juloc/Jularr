using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class ProfileFormatReferencesData : BaseData
    {
        public ProfileFormatReferencesData(long[] formatIds)
        {
            FormatIds = formatIds;
        }

        [DataField(typeof(FormatScoreFields), nameof(FormatScoreFields.FormatIds))]
        public long[] FormatIds { get; }
    }

    internal sealed class ProfileFormatScopeData : BaseData
    {
        public ProfileFormatScopeData(long id, MediaType mediaTypeId)
        {
            Id = id;
            MediaTypeId = mediaTypeId;
        }

        [DataField(typeof(CustomFormatFields), nameof(CustomFormatFields.Id))]
        public long Id { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }
    }
}
