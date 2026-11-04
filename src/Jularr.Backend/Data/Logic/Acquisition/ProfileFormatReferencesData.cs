using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;

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
}
