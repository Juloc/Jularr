using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class ReadCoverCacheParametersV1 : BaseParameters
    {
    }

    // How long and how much of the images of titles that are not Works Jularr keeps.
    public sealed class CoverCacheV1 : BaseResponse
    {
        public CoverCacheV1(int keepDays, int maximumMegabytes, long version)
        {
            KeepDays = keepDays;
            MaximumMegabytes = maximumMegabytes;
            Version = version;
        }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.KeepDays))]
        public int KeepDays { get; }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.MaximumMegabytes))]
        public int MaximumMegabytes { get; }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.Version))]
        public long Version { get; }
    }

    public sealed class UpdateCoverCacheDataV1 : BaseData
    {
        public UpdateCoverCacheDataV1(int keepDays, int maximumMegabytes, long version)
        {
            KeepDays = keepDays;
            MaximumMegabytes = maximumMegabytes;
            Version = version;
        }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.KeepDays))]
        public int KeepDays { get; }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.MaximumMegabytes))]
        public int MaximumMegabytes { get; }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.Version))]
        public long Version { get; }
    }
}
