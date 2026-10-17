using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class ArtworkExportLocationData : BaseData
    {
        public ArtworkExportLocationData(long libraryRootId, long storageMountId, MediaType mediaTypeId, string rootRelativePath, string relativePath)
        {
            LibraryRootId = libraryRootId;
            StorageMountId = storageMountId;
            MediaTypeId = mediaTypeId;
            RootRelativePath = rootRelativePath;
            RelativePath = relativePath;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RootRelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }
    }

    internal sealed class ArtworkExportFolderData : BaseData
    {
        public ArtworkExportFolderData(long workId, long libraryRootId, string folder)
        {
            WorkId = workId;
            LibraryRootId = libraryRootId;
            Folder = folder;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.Folder))]
        public string Folder { get; }
    }

    internal sealed class ArtworkExclusiveFolderData : BaseData
    {
        public ArtworkExclusiveFolderData(bool isExclusive)
        {
            IsExclusive = isExclusive;
        }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.IsExclusive))]
        public bool IsExclusive { get; }
    }

    internal sealed class ArtworkNasExportSettingsData : BaseData
    {
        public ArtworkNasExportSettingsData(bool exportToNas, NasArtworkMode nasModeId, bool progressiveJxl)
        {
            ExportToNas = exportToNas;
            NasModeId = nasModeId;
            ProgressiveJxl = progressiveJxl;
        }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.ExportToNas))]
        public bool ExportToNas { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.NasModeId))]
        public NasArtworkMode NasModeId { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.ProgressiveJxl))]
        public bool ProgressiveJxl { get; }
    }

    internal sealed class ArtworkExportFactData : BaseData
    {
        public ArtworkExportFactData(string factKey, string value)
        {
            FactKey = factKey;
            Value = value;
        }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Fact))]
        public string FactKey { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.SourceUrl))]
        public string Value { get; }
    }
}
