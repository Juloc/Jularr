using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class ReadCoverCacheParametersV1 : BaseParameters
    {
    }

    // How long and how much of the images of titles that are not Works Jularr keeps, and the one format covers are kept in; the active format differs from the chosen one where JPEG XL cannot be made.
    public sealed class CoverCacheV1 : BaseResponse
    {
        public CoverCacheV1(int keepDays, int maximumMegabytes, CoverFormat formatId, CoverFormat activeFormatId, long version)
        {
            KeepDays = keepDays;
            MaximumMegabytes = maximumMegabytes;
            FormatId = formatId;
            ActiveFormatId = activeFormatId;
            Version = version;
        }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.KeepDays))]
        public int KeepDays { get; }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.MaximumMegabytes))]
        public int MaximumMegabytes { get; }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.FormatId))]
        public CoverFormat FormatId { get; }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.FormatId))]
        public CoverFormat ActiveFormatId { get; }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.Version))]
        public long Version { get; }
    }

    public sealed class UpdateCoverCacheDataV1 : BaseData
    {
        public UpdateCoverCacheDataV1(int keepDays, int maximumMegabytes, CoverFormat formatId, long version)
        {
            KeepDays = keepDays;
            MaximumMegabytes = maximumMegabytes;
            FormatId = formatId;
            Version = version;
        }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.KeepDays))]
        public int KeepDays { get; }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.MaximumMegabytes))]
        public int MaximumMegabytes { get; }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.FormatId))]
        public CoverFormat FormatId { get; }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.Version))]
        public long Version { get; }
    }
}
