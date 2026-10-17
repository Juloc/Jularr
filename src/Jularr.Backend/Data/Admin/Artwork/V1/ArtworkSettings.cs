using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Admin.Artwork.V1
{
    public sealed class ArtworkSettingsParametersV1 : BaseParameters
    {
    }

    public sealed class ArtworkSettingsDetailsV1 : BaseResponse
    {
        public ArtworkSettingsDetailsV1(NasArtworkMode nasModeId, ArtworkCacheFormat cacheFormatId,
            ArtworkQuality cacheQualityId, bool progressiveJxl, bool exportToNas,
            long cacheLimitBytes, long version)
        {
            NasModeId = nasModeId;
            CacheFormatId = cacheFormatId;
            CacheQualityId = cacheQualityId;
            ProgressiveJxl = progressiveJxl;
            ExportToNas = exportToNas;
            CacheLimitBytes = cacheLimitBytes;
            Version = version;
        }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.NasModeId))]
        public NasArtworkMode NasModeId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheFormatId))]
        public ArtworkCacheFormat CacheFormatId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheQualityId))]
        public ArtworkQuality CacheQualityId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.ProgressiveJxl))]
        public bool ProgressiveJxl { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.ExportToNas))]
        public bool ExportToNas { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheLimitBytes))]
        public long CacheLimitBytes { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Version))]
        public long Version { get; }
    }

    public sealed class UpdateArtworkSettingsDataV1 : BaseData
    {
        public UpdateArtworkSettingsDataV1(NasArtworkMode nasModeId, ArtworkCacheFormat cacheFormatId,
            ArtworkQuality cacheQualityId, bool progressiveJxl, bool exportToNas,
            long cacheLimitBytes, long version)
        {
            NasModeId = nasModeId;
            CacheFormatId = cacheFormatId;
            CacheQualityId = cacheQualityId;
            ProgressiveJxl = progressiveJxl;
            ExportToNas = exportToNas;
            CacheLimitBytes = cacheLimitBytes;
            Version = version;
        }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.NasModeId))]
        public NasArtworkMode NasModeId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheFormatId))]
        public ArtworkCacheFormat CacheFormatId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheQualityId))]
        public ArtworkQuality CacheQualityId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.ProgressiveJxl))]
        public bool ProgressiveJxl { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.ExportToNas))]
        public bool ExportToNas { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheLimitBytes))]
        public long CacheLimitBytes { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Version))]
        public long Version { get; }
    }
}
