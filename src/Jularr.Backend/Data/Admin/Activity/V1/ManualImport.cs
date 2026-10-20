using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Admin.Activity.V1
{
    public sealed class ManualImportWorksParametersV1 : BaseParameters
    {
        public ManualImportWorksParametersV1(string query)
        {
            Query = query;
        }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.Query))]
        public string Query { get; }
    }

    public sealed class ManualImportParametersV1 : BaseParameters
    {
        public ManualImportParametersV1(long wantedItemId)
        {
            WantedItemId = wantedItemId;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }
    }

    // A film or series a folder can be imported to.
    public sealed class ManualImportWorkV1 : BaseResponse
    {
        public ManualImportWorkV1(long wantedItemId, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string canonicalTitle, int? releaseYear)
        {
            WantedItemId = wantedItemId;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            CanonicalTitle = canonicalTitle;
            ReleaseYear = releaseYear;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string CanonicalTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }
    }

    public sealed class ReadManualImportDataV1 : BaseData
    {
        public ReadManualImportDataV1(long storageMountId, string relativePath)
        {
            StorageMountId = storageMountId;
            RelativePath = relativePath;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RelativePath { get; }
    }

    // A video of the folder with what its name says it is and the name it would be given in the library.
    public sealed class ManualImportFileV1 : BaseResponse
    {
        public ManualImportFileV1(string path, long sizeBytes, int? season, int? episode, string? qualityKey, string? targetName)
        {
            Path = path;
            SizeBytes = sizeBytes;
            Season = season;
            Episode = episode;
            QualityKey = qualityKey;
            TargetName = targetName;
        }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.FilePath))]
        public string Path { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ParentUnitNumber))]
        public int? Season { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.UnitStart))]
        public int? Episode { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.QualityKey))]
        public string? QualityKey { get; }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.TargetName))]
        public string? TargetName { get; }
    }

    public sealed class ManualImportFolderV1 : BaseResponse
    {
        public ManualImportFolderV1(BaseDataList<ManualImportFileV1> files)
        {
            Files = files;
        }

        public BaseDataList<ManualImportFileV1> Files { get; }
    }

    // What a person decided for one file.
    public sealed class ManualImportChoiceV1 : BaseData
    {
        public ManualImportChoiceV1(string path, bool imports, int? season, int? episode)
        {
            Path = path;
            Imports = imports;
            Season = season;
            Episode = episode;
        }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.FilePath))]
        public string Path { get; }

        // A file left out stays where it is.
        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.Imports))]
        public bool Imports { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ParentUnitNumber))]
        public int? Season { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.UnitStart))]
        public int? Episode { get; }
    }

    public sealed class RunManualImportDataV1 : BaseData
    {
        public RunManualImportDataV1(long storageMountId, string relativePath, ImportTransferMode importTransferModeId, BaseDataList<ManualImportChoiceV1> files)
        {
            StorageMountId = storageMountId;
            RelativePath = relativePath;
            ImportTransferModeId = importTransferModeId;
            Files = files;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.ImportTransferModeId))]
        public ImportTransferMode ImportTransferModeId { get; }

        public BaseDataList<ManualImportChoiceV1> Files { get; }
    }

    public sealed class ManualImportResultV1 : BaseResponse
    {
        public ManualImportResultV1(int imported)
        {
            Imported = imported;
        }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.ImportedCount))]
        public int Imported { get; }
    }
}
