using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Playback;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Logic.Playback
{
    internal sealed class GameSourceData : BaseData
    {
        public GameSourceData(long mediaAssetId, long workId, string title, string relativePath, int? igdbId, long playedMs)
        {
            MediaAssetId = mediaAssetId;
            WorkId = workId;
            Title = title;
            RelativePath = relativePath;
            IgdbId = igdbId;
            PlayedMs = playedMs;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.IgdbId))]
        public int? IgdbId { get; }

        [DataField(typeof(GameFields), nameof(GameFields.PlayedMs))]
        public long PlayedMs { get; }
    }

    internal sealed class GameLibraryFileData : BaseData
    {
        public GameLibraryFileData(long storedFileId, string relativePath, long sizeBytes, int? igdbId, long? mediaAssetId, long? workId, string? title)
        {
            StoredFileId = storedFileId;
            RelativePath = relativePath;
            SizeBytes = sizeBytes;
            IgdbId = igdbId;
            MediaAssetId = mediaAssetId;
            WorkId = workId;
            Title = title;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.IgdbId))]
        public int? IgdbId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id), false)]
        public long? MediaAssetId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle), false)]
        public string? Title { get; }
    }

    internal sealed class GameStoredFileSourceData : BaseData
    {
        public GameStoredFileSourceData(string relativePath, string rootRelativePath, long storageMountId, int? igdbId)
        {
            RelativePath = relativePath;
            RootRelativePath = rootRelativePath;
            StorageMountId = storageMountId;
            IgdbId = igdbId;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RootRelativePath { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.IgdbId))]
        public int? IgdbId { get; }
    }

    internal sealed class GameStoredFileLookupData : BaseData
    {
        public GameStoredFileLookupData(long profileId, long storedFileId)
        {
            ProfileId = profileId;
            StoredFileId = storedFileId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }
    }

    internal sealed class EmulatedPlatformsData : BaseData
    {
        public EmulatedPlatformsData(int[] emulatedPlatforms)
        {
            EmulatedPlatforms = emulatedPlatforms;
        }

        [DataField(typeof(GameFields), nameof(GameFields.EmulatedPlatforms))]
        public int[] EmulatedPlatforms { get; }
    }

    internal sealed class GameLookupData : BaseData
    {
        public GameLookupData(long profileId, long mediaAssetId, int[] emulatedPlatforms)
        {
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            EmulatedPlatforms = emulatedPlatforms;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(GameFields), nameof(GameFields.EmulatedPlatforms))]
        public int[] EmulatedPlatforms { get; }
    }

    internal sealed class GameSaveLookupData : BaseData
    {
        public GameSaveLookupData(long profileId, long mediaAssetId, GameSaveType gameSaveTypeId)
        {
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            GameSaveTypeId = gameSaveTypeId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(GameFields), nameof(GameFields.GameSaveTypeId))]
        public GameSaveType GameSaveTypeId { get; }
    }

    internal sealed class GameSaveWriteData : BaseData
    {
        public GameSaveWriteData(long profileId, long mediaAssetId, GameSaveType gameSaveTypeId, byte[] data, int[] emulatedPlatforms)
        {
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            GameSaveTypeId = gameSaveTypeId;
            Data = data;
            EmulatedPlatforms = emulatedPlatforms;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(GameFields), nameof(GameFields.GameSaveTypeId))]
        public GameSaveType GameSaveTypeId { get; }

        [DataField(typeof(GameFields), nameof(GameFields.Data))]
        public byte[] Data { get; }

        [DataField(typeof(GameFields), nameof(GameFields.EmulatedPlatforms))]
        public int[] EmulatedPlatforms { get; }
    }

    internal sealed class GamePlayData : BaseData
    {
        public GamePlayData(long profileId, long mediaAssetId, int seconds, int[] emulatedPlatforms)
        {
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            Seconds = seconds;
            EmulatedPlatforms = emulatedPlatforms;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(GameFields), nameof(GameFields.PlayedSeconds))]
        public int Seconds { get; }

        [DataField(typeof(GameFields), nameof(GameFields.EmulatedPlatforms))]
        public int[] EmulatedPlatforms { get; }
    }
}
