using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Library
{
    // One known library file with the identity that names it.
    internal sealed class LibraryRenameFileData : BaseData
    {
        public LibraryRenameFileData(long id, long libraryRootId, string libraryRootName, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string relativePath, DateTime? missingSince,
            long? mediaAssetId, string? canonicalTitle, int? releaseYear, string? author, WorkUnitType? workUnitTypeId, decimal? unitNumber, string? unitTitle, decimal? parentUnitNumber)
        {
            Id = id;
            LibraryRootId = libraryRootId;
            LibraryRootName = libraryRootName;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            RelativePath = relativePath;
            MissingSince = missingSince;
            MediaAssetId = mediaAssetId;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
            Author = author;
            WorkUnitTypeId = workUnitTypeId;
            UnitNumber = unitNumber;
            UnitTitle = unitTitle;
            ParentUnitNumber = parentUnitNumber;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long Id { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Name))]
        public string LibraryRootName { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.MissingSince))]
        public DateTime? MissingSince { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id), false)]
        public long? MediaAssetId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle), false)]
        public string? CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Author))]
        public string? Author { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.WorkUnitTypeId), false)]
        public WorkUnitType? WorkUnitTypeId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? UnitNumber { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitTitle))]
        public string? UnitTitle { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.UnitNumber), false)]
        public decimal? ParentUnitNumber { get; }
    }

    // A file move the Owner confirms; subtitles name the media file they follow.
    internal sealed class LibraryRenameData : BaseData
    {
        public LibraryRenameData(long storedFileId, long mediaStoredFileId, long libraryRootId, string libraryRootName, string relativePath, string targetPath)
        {
            StoredFileId = storedFileId;
            MediaStoredFileId = mediaStoredFileId;
            LibraryRootId = libraryRootId;
            LibraryRootName = libraryRootName;
            RelativePath = relativePath;
            TargetPath = targetPath;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long MediaStoredFileId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Name))]
        public string LibraryRootName { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.TargetPath))]
        public string TargetPath { get; }
    }
}
