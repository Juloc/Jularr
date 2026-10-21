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
        public ManualImportFileV1(string path, long sizeBytes, int? season, int? episode, string? qualityKey, string? targetName, string? languages = null)
        {
            Languages = languages;
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

        // The audio languages its name states, such as "DE+EN"; a name that states none leaves it empty.
        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.Languages))]
        public string? Languages { get; }
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
        public ManualImportChoiceV1(string path, bool imports, int? season, int? episode, long? wantedItemId = null, string? qualityKey = null)
        {
            Path = path;
            Imports = imports;
            Season = season;
            Episode = episode;
            WantedItemId = wantedItemId;
            QualityKey = qualityKey;
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

        // Another film or series than the one the import is for; a download keeps its own.
        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id), false)]
        public long? WantedItemId { get; }

        // The quality the person says the file has; without one its name decides.
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.QualityKey), false)]
        public string? QualityKey { get; }
    }

    // The names the library would give files as their owners decided, to show before anything is placed.
    public sealed class PreviewManualImportDataV1 : BaseData
    {
        public PreviewManualImportDataV1(BaseDataList<ManualImportChoiceV1> files)
        {
            Files = files;
        }

        public BaseDataList<ManualImportChoiceV1> Files { get; }
    }

    public sealed class ManualImportNameV1 : BaseResponse
    {
        public ManualImportNameV1(string path, long wantedItemId, MediaType mediaTypeId, string workTitle, string? targetName)
        {
            Path = path;
            WantedItemId = wantedItemId;
            MediaTypeId = mediaTypeId;
            WorkTitle = workTitle;
            TargetName = targetName;
        }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.FilePath))]
        public string Path { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string WorkTitle { get; }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.TargetName))]
        public string? TargetName { get; }
    }

    // The fields of the match dialog: the film or series, the numbers and the quality a person sets for one file, and the words to search them by.
    public sealed class ManualImportMatchV1 : BaseData
    {
        public ManualImportMatchV1(string? matchQuery, long? matchedWantedItemId, int? matchedSeason, int? matchedEpisode, string? matchedQualityKey)
        {
            MatchQuery = matchQuery;
            MatchedWantedItemId = matchedWantedItemId;
            MatchedSeason = matchedSeason;
            MatchedEpisode = matchedEpisode;
            MatchedQualityKey = matchedQualityKey;
        }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.Query), false)]
        public string? MatchQuery { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id), false)]
        public long? MatchedWantedItemId { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.ParentUnitNumber))]
        public int? MatchedSeason { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.UnitStart))]
        public int? MatchedEpisode { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.QualityKey), false)]
        public string? MatchedQualityKey { get; }
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

    // A waiting download's finished files with what their names say.
    public sealed class ManualImportDownloadV1 : BaseResponse
    {
        public ManualImportDownloadV1(long wantedItemId, BaseDataList<ManualImportFileV1> files)
        {
            WantedItemId = wantedItemId;
            Files = files;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        public BaseDataList<ManualImportFileV1> Files { get; }
    }

    public sealed class RunManualImportDownloadDataV1 : BaseData
    {
        public RunManualImportDownloadDataV1(ImportTransferMode importTransferModeId, BaseDataList<ManualImportChoiceV1> files)
        {
            ImportTransferModeId = importTransferModeId;
            Files = files;
        }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.ImportTransferModeId))]
        public ImportTransferMode ImportTransferModeId { get; }

        public BaseDataList<ManualImportChoiceV1> Files { get; }
    }

    // What became of one chosen file: the name it has in the library, or the reason a person must settle.
    public sealed class ManualImportOutcomeV1 : BaseResponse
    {
        public ManualImportOutcomeV1(string path, string workTitle, string? failureKey)
        {
            Path = path;
            WorkTitle = workTitle;
            FailureKey = failureKey;
        }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.FilePath))]
        public string Path { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string WorkTitle { get; }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.FailureKey))]
        public string? FailureKey { get; }
    }

    public sealed class ManualImportResultV1 : BaseResponse
    {
        public ManualImportResultV1(int imported, BaseDataList<ManualImportOutcomeV1>? files = null)
        {
            Imported = imported;
            Files = files ?? new([]);
        }

        public BaseDataList<ManualImportOutcomeV1> Files { get; }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.ImportedCount))]
        public int Imported { get; }
    }
}
