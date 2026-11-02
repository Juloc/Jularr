using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Playback;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Logic.Playback
{
    internal sealed class PlaybackSourceParameters : BaseData
    {
        public PlaybackSourceParameters(long mediaAssetId, long profileId, Guid? sessionPublicId = null)
        {
            MediaAssetId = mediaAssetId;
            ProfileId = profileId;
            SessionPublicId = sessionPublicId;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        // The device asking, for its music quality.
        [DataField(typeof(Fields.Account.AccountSessionFields), nameof(Fields.Account.AccountSessionFields.PublicId), false)]
        public Guid? SessionPublicId { get; }
    }

    internal sealed class PlaybackSourceData : BaseData
    {
        public PlaybackSourceData(long storedFileId, string relativePath, string rootRelativePath, long storageMountId,
            string? wakeMacAddress, string? wakeBroadcastAddress, bool wakeOnDemand, long? durationMs, long? resumePositionMs, long? bitRate, PlaybackQuality playbackQuality)
        {
            BitRate = bitRate;
            PlaybackQuality = playbackQuality;
            StoredFileId = storedFileId;
            RelativePath = relativePath;
            RootRelativePath = rootRelativePath;
            StorageMountId = storageMountId;
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

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.BitRate))]
        public long? BitRate { get; }

        // The playing device's music quality.
        [DataField(typeof(Fields.Account.AccountSessionFields), nameof(Fields.Account.AccountSessionFields.PlaybackQuality))]
        public PlaybackQuality PlaybackQuality { get; }
    }

    internal sealed class PlaybackTrackData : BaseData
    {
        public PlaybackTrackData(int streamIndex, MediaTrackType mediaTrackTypeId, string? codec, bool isDefault, string? language = null, int? height = null)
        {
            StreamIndex = streamIndex;
            MediaTrackTypeId = mediaTrackTypeId;
            Codec = codec;
            IsDefault = isDefault;
            Language = language;
            Height = height;
        }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.StreamIndex))]
        public int StreamIndex { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.MediaTrackTypeId))]
        public MediaTrackType MediaTrackTypeId { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Codec))]
        public string? Codec { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.IsDefault))]
        public bool IsDefault { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Language))]
        public string? Language { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Height))]
        public int? Height { get; }
    }

    internal sealed class PlaybackSessionCreationData : BaseData
    {
        public PlaybackSessionCreationData(Guid publicId, long profileId, long mediaAssetId, long storedFileId, PlaybackModeType playbackModeTypeId, long positionMs, Guid ownerPublicId, long ownerRevision)
        {
            PublicId = publicId;
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            StoredFileId = storedFileId;
            PlaybackModeTypeId = playbackModeTypeId;
            PositionMs = positionMs;
            OwnerPublicId = ownerPublicId;
            OwnerRevision = ownerRevision;
        }

        // Chosen before the session exists, so the server's budget can hold its conversion first.
        [DataField(typeof(Fields.Account.AccountSessionFields), nameof(Fields.Account.AccountSessionFields.PublicId))]
        public Guid PublicId { get; }

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

        [DataField(typeof(Fields.Account.AccountSessionFields), nameof(Fields.Account.AccountSessionFields.PublicId))]
        public Guid OwnerPublicId { get; }

        [DataField(typeof(Fields.Account.AccountSessionFields), nameof(Fields.Account.AccountSessionFields.RotationRevision))]
        public long OwnerRevision { get; }
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
        public PlaybackStreamData(string relativePath, string rootRelativePath, long storageMountId)
        {
            RelativePath = relativePath;
            RootRelativePath = rootRelativePath;
            StorageMountId = storageMountId;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RootRelativePath { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

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

    // An audio or subtitle stream of the media file, or a subtitle file beside it.
    internal sealed class PlaybackTrackRowData : BaseData
    {
        public PlaybackTrackRowData(int? streamIndex, long? storedFileId, string? relativePath, MediaTrackType mediaTrackTypeId, string? codec, string? language, string? title, int? channels,
            bool isDefault, bool isForced)
        {
            StreamIndex = streamIndex;
            StoredFileId = storedFileId;
            RelativePath = relativePath;
            MediaTrackTypeId = mediaTrackTypeId;
            Codec = codec;
            Language = language;
            Title = title;
            Channels = channels;
            IsDefault = isDefault;
            IsForced = isForced;
        }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.StreamIndex), false)]
        public int? StreamIndex { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id), false)]
        public long? StoredFileId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath), false)]
        public string? RelativePath { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.MediaTrackTypeId))]
        public MediaTrackType MediaTrackTypeId { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Codec))]
        public string? Codec { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Language))]
        public string? Language { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Title))]
        public string? Title { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Channels))]
        public int? Channels { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.IsDefault))]
        public bool IsDefault { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.IsForced))]
        public bool IsForced { get; }
    }

    // Where a subtitle comes from: a subtitle file, or a text stream of the media file.
    // A stored marker with where it came from and how long the file is, for the player to leave out one that cannot be right.
    internal sealed class PlaybackMarkerRowData : BaseData
    {
        public PlaybackMarkerRowData(MediaMarkerKind mediaMarkerKindTypeId, MediaMarkerSource mediaMarkerSourceTypeId, long startMs, long endMs, long? durationMs)
        {
            MediaMarkerKindTypeId = mediaMarkerKindTypeId;
            MediaMarkerSourceTypeId = mediaMarkerSourceTypeId;
            StartMs = startMs;
            EndMs = endMs;
            DurationMs = durationMs;
        }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.MediaMarkerKindTypeId))]
        public MediaMarkerKind MediaMarkerKindTypeId { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.MediaMarkerSourceTypeId))]
        public MediaMarkerSource MediaMarkerSourceTypeId { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.StartMs))]
        public long StartMs { get; }

        [DataField(typeof(MediaMarkerFields), nameof(MediaMarkerFields.EndMs))]
        public long EndMs { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.DurationMs))]
        public long? DurationMs { get; }
    }

    internal sealed class PlaybackSubtitleSourceData : BaseData
    {
        public PlaybackSubtitleSourceData(long storedFileId, string relativePath, DateTime modifiedAt, string rootRelativePath, long storageMountId, string? codec)
        {
            StoredFileId = storedFileId;
            RelativePath = relativePath;
            ModifiedAt = modifiedAt;
            RootRelativePath = rootRelativePath;
            StorageMountId = storageMountId;
            Codec = codec;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RootRelativePath { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Codec))]
        public string? Codec { get; }
    }

    internal sealed class PlaybackSubtitleLookupData : BaseData
    {
        public PlaybackSubtitleLookupData(long mediaAssetId, long profileId, int? streamIndex, long? storedFileId)
        {
            MediaAssetId = mediaAssetId;
            ProfileId = profileId;
            StreamIndex = streamIndex;
            StoredFileId = storedFileId;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.StreamIndex), false)]
        public int? StreamIndex { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id), false)]
        public long? StoredFileId { get; }
    }

    internal sealed class TrickplayFileData : BaseData
    {
        public TrickplayFileData(int trickplayFrames, int width, int height)
        {
            TrickplayFrames = trickplayFrames;
            Width = width;
            Height = height;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.TrickplayFrames))]
        public int TrickplayFrames { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Width))]
        public int Width { get; }

        [DataField(typeof(MediaTrackFields), nameof(MediaTrackFields.Height))]
        public int Height { get; }
    }

    internal sealed class TrickplaySheetFileData : BaseData
    {
        public TrickplaySheetFileData(long id, int? trickplayFrames)
        {
            Id = id;
            TrickplayFrames = trickplayFrames;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.TrickplayFrames), false)]
        public int? TrickplayFrames { get; }
    }
}
