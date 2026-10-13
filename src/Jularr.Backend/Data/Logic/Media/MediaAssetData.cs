using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class WorkUnitData : BaseData
    {
        public WorkUnitData(long workId, long? parentWorkUnitId, WorkUnitType workUnitTypeId, decimal number)
        {
            WorkId = workId;
            ParentWorkUnitId = parentWorkUnitId;
            WorkUnitTypeId = workUnitTypeId;
            Number = number;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitId), false)]
        public long? ParentWorkUnitId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitTypeId))]
        public WorkUnitType WorkUnitTypeId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber))]
        public decimal Number { get; }
    }

    internal sealed class MediaAssetCreationData : BaseData
    {
        public MediaAssetCreationData(long workId, long workEditionId, long? workUnitId, MediaAssetType mediaAssetTypeId)
        {
            WorkId = workId;
            WorkEditionId = workEditionId;
            WorkUnitId = workUnitId;
            MediaAssetTypeId = mediaAssetTypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkEditionId))]
        public long WorkEditionId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitId), false)]
        public long? WorkUnitId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.MediaAssetTypeId))]
        public MediaAssetType MediaAssetTypeId { get; }
    }

    internal sealed class StoredFileLinkData : BaseData
    {
        public StoredFileLinkData(long storedFileId, long libraryRootId, string stem, long? mediaAssetId)
        {
            StoredFileId = storedFileId;
            LibraryRootId = libraryRootId;
            Stem = stem;
            MediaAssetId = mediaAssetId;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string Stem { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id), false)]
        public long? MediaAssetId { get; }
    }

    internal sealed class WorkIdData : BaseData
    {
        public WorkIdData(long id)
        {
            Id = id;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }
    }

    internal sealed class WorkPlatformsData : BaseData
    {
        public WorkPlatformsData(long workId, string platforms)
        {
            WorkId = workId;
            Platforms = platforms;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        // JSON list of the provider's platforms.
        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Platforms))]
        public string Platforms { get; }
    }

    internal sealed class GameReleaseData : BaseData
    {
        public GameReleaseData(long mediaAssetId, long gamePlatformId)
        {
            MediaAssetId = mediaAssetId;
            GamePlatformId = gamePlatformId;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Id))]
        public long GamePlatformId { get; }
    }

    internal sealed class WorkLookupData : BaseData
    {
        public WorkLookupData(long workId, MediaType mediaTypeId)
        {
            WorkId = workId;
            MediaTypeId = mediaTypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }
    }
}
