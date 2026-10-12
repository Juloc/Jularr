using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Playback;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Logic.Playback
{
    internal sealed class PlaybackSourceParameters : BaseData
    {
        public PlaybackSourceParameters(long mediaAssetId, long profileId)
        {
            MediaAssetId = mediaAssetId;
            ProfileId = profileId;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }
    }

    internal sealed class PlaybackSourceData : BaseData
    {
        public PlaybackSourceData(long storedFileId, string relativePath, string rootRelativePath, long storageMountId, StorageProtocolType storageProtocolTypeId, string sharePath,
            string? wakeMacAddress, string? wakeBroadcastAddress, bool wakeOnDemand, long? durationMs, long? resumePositionMs)
        {
            StoredFileId = storedFileId;
            RelativePath = relativePath;
            RootRelativePath = rootRelativePath;
            StorageMountId = storageMountId;
            StorageProtocolTypeId = storageProtocolTypeId;
            SharePath = sharePath;
            WakeMacAddress = wakeMacAddress;
            WakeBroadcastAddress = wakeBroadcastAddress;
            WakeOnDemand = wakeOnDemand;
            DurationMs = durationMs;
            ResumePositionMs = resumePositionMs;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RootRelativePath { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.StorageProtocolTypeId))]
        public StorageProtocolType StorageProtocolTypeId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.SharePath))]
        public string SharePath { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.WakeMacAddress))]
        public string? WakeMacAddress { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.WakeBroadcastAddress))]
        public string? WakeBroadcastAddress { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.WakeOnDemand))]
        public bool WakeOnDemand { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.DurationMs))]
        public long? DurationMs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs), false)]
        public long? ResumePositionMs { get; }
    }

    internal sealed class PlaybackTrackData : BaseData
    {
        public PlaybackTrackData(int streamIndex, MediaTrackType mediaTrackTypeId, string? codec, bool isDefault)
        {
            StreamIndex = streamIndex;
            MediaTrackTypeId = mediaTrackTypeId;
            Codec = codec;
            IsDefault = isDefault;
        }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.StreamIndex))]
        public int StreamIndex { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.MediaTrackTypeId))]
        public MediaTrackType MediaTrackTypeId { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Codec))]
        public string? Codec { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.IsDefault))]
        public bool IsDefault { get; }
    }

    internal sealed class PlaybackSessionCreationData : BaseData
    {
        public PlaybackSessionCreationData(long profileId, long mediaAssetId, long storedFileId, PlaybackModeType playbackModeTypeId, long positionMs)
        {
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            StoredFileId = storedFileId;
            PlaybackModeTypeId = playbackModeTypeId;
            PositionMs = positionMs;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PlaybackModeTypeId))]
        public PlaybackModeType PlaybackModeTypeId { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs))]
        public long PositionMs { get; }
    }

    internal sealed class PlaybackSessionIdData : BaseData
    {
        public PlaybackSessionIdData(Guid publicId)
        {
            PublicId = publicId;
        }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PlaybackSessionId))]
        public Guid PublicId { get; }
    }

    internal sealed class PlaybackSessionLookupData : BaseData
    {
        public PlaybackSessionLookupData(Guid playbackSessionId, long profileId)
        {
            PlaybackSessionId = playbackSessionId;
            ProfileId = profileId;
        }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PlaybackSessionId))]
        public Guid PlaybackSessionId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }
    }

    internal sealed class PlaybackStreamData : BaseData
    {
        public PlaybackStreamData(string relativePath, string rootRelativePath, long storageMountId, StorageProtocolType storageProtocolTypeId, string sharePath)
        {
            RelativePath = relativePath;
            RootRelativePath = rootRelativePath;
            StorageMountId = storageMountId;
            StorageProtocolTypeId = storageProtocolTypeId;
            SharePath = sharePath;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RootRelativePath { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.StorageProtocolTypeId))]
        public StorageProtocolType StorageProtocolTypeId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.SharePath))]
        public string SharePath { get; }
    }

    internal sealed class PlaybackUpdateData : BaseData
    {
        public PlaybackUpdateData(Guid playbackSessionId, long profileId, long positionMs, long? durationMs, bool isEnded, bool isCompleted)
        {
            PlaybackSessionId = playbackSessionId;
            ProfileId = profileId;
            PositionMs = positionMs;
            DurationMs = durationMs;
            IsEnded = isEnded;
            IsCompleted = isCompleted;
        }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PlaybackSessionId))]
        public Guid PlaybackSessionId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs))]
        public long PositionMs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.DurationMs))]
        public long? DurationMs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.IsEnded))]
        public bool IsEnded { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.IsCompleted))]
        public bool IsCompleted { get; }
    }
}
