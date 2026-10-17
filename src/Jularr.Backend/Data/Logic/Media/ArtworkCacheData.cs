using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class ArtworkCacheSettingsData : BaseData
    {
        public ArtworkCacheSettingsData(ArtworkQuality cacheQualityId, long cacheLimitBytes, ArtworkCacheFormat cacheFormatId, bool progressiveJxl)
        {
            CacheQualityId = cacheQualityId;
            CacheLimitBytes = cacheLimitBytes;
            CacheFormatId = cacheFormatId;
            ProgressiveJxl = progressiveJxl;
        }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheQualityId))]
        public ArtworkQuality CacheQualityId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheLimitBytes))]
        public long CacheLimitBytes { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheFormatId))]
        public ArtworkCacheFormat CacheFormatId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.ProgressiveJxl))]
        public bool ProgressiveJxl { get; }
    }

    internal sealed class ArtworkCacheCandidateData : BaseData
    {
        public ArtworkCacheCandidateData(long artworkId, long workId, long? workUnitId, ArtworkSlot slotId, string sourceKey, long? sourceSizeBytes)
        {
            ArtworkId = artworkId;
            WorkId = workId;
            WorkUnitId = workUnitId;
            SlotId = slotId;
            SourceKey = sourceKey;
            SourceSizeBytes = sourceSizeBytes;
        }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.ArtworkId))]
        public long ArtworkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id), false)]
        public long? WorkUnitId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.SlotId))]
        public ArtworkSlot SlotId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string SourceKey { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.SourceSizeBytes))]
        public long? SourceSizeBytes { get; }
    }

    internal sealed class ArtworkCacheRootData : BaseData
    {
        public ArtworkCacheRootData(long libraryRootId)
        {
            LibraryRootId = libraryRootId;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }
    }

    internal sealed class ArtworkRenditionSaveData : BaseData
    {
        public ArtworkRenditionSaveData(long artworkId, long workId, long? workUnitId, ArtworkSlot slotId, ArtworkFormat formatId, bool isProgressive, int width, int height, string cacheKey, string sourceHash, string contentHash, long sizeBytes,
            ArtworkQuality qualityId, byte[]? fallbackBytes, string? fallbackHash)
        {
            ArtworkId = artworkId;
            WorkId = workId;
            WorkUnitId = workUnitId;
            SlotId = slotId;
            FormatId = formatId;
            IsProgressive = isProgressive;
            SourceHash = sourceHash;
            Width = width;
            Height = height;
            CacheKey = cacheKey;
            ContentHash = contentHash;
            SizeBytes = sizeBytes;
            QualityId = qualityId;
            FallbackBytes = fallbackBytes;
            FallbackHash = fallbackHash;
        }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.ArtworkId))]
        public long ArtworkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkUnitFields), nameof(WorkUnitFields.Id), false)]
        public long? WorkUnitId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.SlotId))]
        public ArtworkSlot SlotId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.ContentHash))]
        public string SourceHash { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.FormatId))]
        public ArtworkFormat FormatId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.IsProgressive))]
        public bool IsProgressive { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.Width))]
        public int Width { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.Height))]
        public int Height { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheKey))]
        public string CacheKey { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.ContentHash))]
        public string ContentHash { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.SourceSizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheQualityId))]
        public ArtworkQuality QualityId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.FallbackBytes))]
        public byte[]? FallbackBytes { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.ContentHash), false)]
        public string? FallbackHash { get; }
    }


    // Ordered oldest first by the database. Content keys may be shared across Works.
    internal sealed class ArtworkStoredRenditionData : BaseData
    {
        public ArtworkStoredRenditionData(string cacheKey, ArtworkFormat formatId)
        {
            CacheKey = cacheKey;
            FormatId = formatId;
        }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheKey))]
        public string CacheKey { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.FormatId))]
        public ArtworkFormat FormatId { get; }
    }

    internal sealed class ArtworkPreviewData : BaseData
    {
        public ArtworkPreviewData(string? cacheKey, string? jxlCacheKey, byte[]? fallbackBytes)
        {
            CacheKey = cacheKey;
            JxlCacheKey = jxlCacheKey;
            FallbackBytes = fallbackBytes;
        }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheKey), false)]
        public string? CacheKey { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheKey), false)]
        public string? JxlCacheKey { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.FallbackBytes), false)]
        public byte[]? FallbackBytes { get; }
    }
}
