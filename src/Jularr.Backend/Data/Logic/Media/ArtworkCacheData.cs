using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class ArtworkCacheSettingsData : BaseData
    {
        public ArtworkCacheSettingsData(ArtworkQuality cacheQualityId, long cacheLimitBytes)
        {
            CacheQualityId = cacheQualityId;
            CacheLimitBytes = cacheLimitBytes;
        }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheQualityId))]
        public ArtworkQuality CacheQualityId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheLimitBytes))]
        public long CacheLimitBytes { get; }
    }

    internal sealed class ArtworkCacheCandidateData : BaseData
    {
        public ArtworkCacheCandidateData(long artworkId, long workId, ArtworkSlot slotId, string sourceKey, long? sourceSizeBytes)
        {
            ArtworkId = artworkId;
            WorkId = workId;
            SlotId = slotId;
            SourceKey = sourceKey;
            SourceSizeBytes = sourceSizeBytes;
        }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.ArtworkId))]
        public long ArtworkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

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
        public ArtworkRenditionSaveData(long artworkId, long workId, ArtworkSlot slotId, int width, int height, string cacheKey, string sourceHash, string contentHash, long sizeBytes,
            ArtworkQuality qualityId, byte[]? fallbackBytes)
        {
            ArtworkId = artworkId;
            WorkId = workId;
            SlotId = slotId;
            SourceHash = sourceHash;
            Width = width;
            Height = height;
            CacheKey = cacheKey;
            ContentHash = contentHash;
            SizeBytes = sizeBytes;
            QualityId = qualityId;
            FallbackBytes = fallbackBytes;
        }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.ArtworkId))]
        public long ArtworkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.SlotId))]
        public ArtworkSlot SlotId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.ContentHash))]
        public string SourceHash { get; }

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
    }

    internal sealed class ArtworkPreviewData : BaseData
    {
        public ArtworkPreviewData(string? cacheKey, byte[]? fallbackBytes)
        {
            CacheKey = cacheKey;
            FallbackBytes = fallbackBytes;
        }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.CacheKey), false)]
        public string? CacheKey { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.FallbackBytes), false)]
        public byte[]? FallbackBytes { get; }
    }
}
