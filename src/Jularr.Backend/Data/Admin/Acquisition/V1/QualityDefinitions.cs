using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class QualityDefinitionsParametersV1 : BaseParameters
    {
        public QualityDefinitionsParametersV1(MediaType mediaTypeId)
        {
            MediaTypeId = mediaTypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }
    }
    public sealed class QualityDefinitionV1 : BaseData
    {
        public QualityDefinitionV1(string quality, decimal minimumMbPerMinute, decimal? preferredMbPerMinute, decimal? maximumMbPerMinute, long version)
        {
            Quality = quality;
            MinimumMbPerMinute = minimumMbPerMinute;
            PreferredMbPerMinute = preferredMbPerMinute;
            MaximumMbPerMinute = maximumMbPerMinute;
            Version = version;
        }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Quality))]
        public string Quality { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.MinimumSize))]
        public decimal MinimumMbPerMinute { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.OptionalSize))]
        public decimal? PreferredMbPerMinute { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.OptionalSize))]
        public decimal? MaximumMbPerMinute { get; }

        [DataField(typeof(AcquisitionPolicyFields), nameof(AcquisitionPolicyFields.Version))]
        public long Version { get; }
    }
    public sealed class SaveQualityDefinitionsDataV1 : BaseData
    {
        public SaveQualityDefinitionsDataV1(BaseDataList<QualityDefinitionV1> rows)
        {
            Rows = rows;
        }

        public BaseDataList<QualityDefinitionV1> Rows { get; }
        public override void Validate()
        {
            base.Validate();
            if (Rows is null || Rows.Count is < 1 or > 64) throw new DataValidationException(nameof(Rows), "out_of_range");
            Rows.Validate(nameof(Rows));
            if (Rows.Select(row => row.Quality).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Rows.Count) throw new DataValidationException(nameof(Rows), "duplicate_value");
            if (Rows.Any(row => row.PreferredMbPerMinute < row.MinimumMbPerMinute || row.MaximumMbPerMinute < row.MinimumMbPerMinute
                || row.MaximumMbPerMinute < row.PreferredMbPerMinute)) throw new DataValidationException(nameof(Rows), "out_of_range");
        }

    }
}
