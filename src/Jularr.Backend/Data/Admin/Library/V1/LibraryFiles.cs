using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Library.V1
{
    public sealed class ReadLibraryFilesParametersV1 : BaseParameters
    {
        public ReadLibraryFilesParametersV1(long? libraryRootId = null, bool onlyUnmatched = true, int page = 1, int pageSize = 25)
        {
            LibraryRootId = libraryRootId;
            OnlyUnmatched = onlyUnmatched;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id), false)]
        public long? LibraryRootId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.OnlyUnmatched))]
        public bool OnlyUnmatched { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class StoredFileParametersV1 : BaseParameters
    {
        public StoredFileParametersV1(long storedFileId)
        {
            StoredFileId = storedFileId;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }
    }

    public sealed class LibraryFileDetailsV1 : BaseResponse
    {
        public LibraryFileDetailsV1(long id, long libraryRootId, string libraryRootName, MediaType mediaTypeId, string relativePath, long sizeBytes, DateTime? missingSince,
            long? mediaAssetId, long? workId, string? workTitle, string suggestedTitle, long? suggestedWorkId, WorkUnitType? suggestedWorkUnitTypeId, decimal? suggestedUnitNumber,
            decimal? suggestedParentUnitNumber, bool hasOtherVersions)
        {
            HasOtherVersions = hasOtherVersions;
            Id = id;
            LibraryRootId = libraryRootId;
            LibraryRootName = libraryRootName;
            MediaTypeId = mediaTypeId;
            RelativePath = relativePath;
            SizeBytes = sizeBytes;
            MissingSince = missingSince;
            MediaAssetId = mediaAssetId;
            WorkId = workId;
            WorkTitle = workTitle;
            SuggestedTitle = suggestedTitle;
            SuggestedWorkId = suggestedWorkId;
            SuggestedWorkUnitTypeId = suggestedWorkUnitTypeId;
            SuggestedUnitNumber = suggestedUnitNumber;
            SuggestedParentUnitNumber = suggestedParentUnitNumber;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Name))]
        public string LibraryRootName { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.MissingSince))]
        public DateTime? MissingSince { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id), false)]
        public long? MediaAssetId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle), false)]
        public string? WorkTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string SuggestedTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? SuggestedWorkId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitTypeId), false)]
        public WorkUnitType? SuggestedWorkUnitTypeId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? SuggestedUnitNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? SuggestedParentUnitNumber { get; }

        // Another version of the same film, episode or volume is in the library.
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.HasOtherVersions))]
        public bool HasOtherVersions { get; }
    }

    public sealed class AssignLibraryFileDataV1 : BaseData
    {
        public AssignLibraryFileDataV1(long? workId, string? title, WorkUnitType? workUnitTypeId, decimal? unitNumber, decimal? parentUnitNumber, long? gamePlatformId = null)
        {
            GamePlatformId = gamePlatformId;
            WorkId = workId;
            Title = title;
            WorkUnitTypeId = workUnitTypeId;
            UnitNumber = unitNumber;
            ParentUnitNumber = parentUnitNumber;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle), false)]
        public string? Title { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitTypeId), false)]
        public WorkUnitType? WorkUnitTypeId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? UnitNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? ParentUnitNumber { get; }

        // A game's platform; without one its platform folder names it.
        [DataField(typeof(GamePlatformFields), nameof(GamePlatformFields.Id), false)]
        public long? GamePlatformId { get; }
    }

    public sealed class AssignLibraryFileResultV1 : BaseResponse
    {
        public AssignLibraryFileResultV1(long mediaAssetId)
        {
            MediaAssetId = mediaAssetId;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }
    }
}
