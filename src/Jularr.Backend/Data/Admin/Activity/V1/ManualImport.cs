using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Admin.Activity.V1
{
    public sealed class ManualImportWorksParametersV1 : BaseParameters
    {
        public ManualImportWorksParametersV1(string query, bool anyMediaType = false)
        {
            Query = query;
            AnyMediaType = anyMediaType;
        }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.Query))]
        public string Query { get; }

        // Every wanted title, not only films and series (a download added by hand on the Downloads page).
        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.AnyMediaType))]
        public bool AnyMediaType { get; }
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

    // A film, series or book a folder can be imported to.
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

    // A manual import of any folder: every file names its own film, series or book.
    public sealed class ManualImportFolderParametersV1 : BaseParameters
    {
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

    // A video or book file of the folder with the film, series or book it was recognised as, what its name says and the name it would be given in the library; of a book Season is the
    // volume and Episode a manga chapter. A file without a problem can be imported as it is; one with a problem says what a person must check.
    public sealed class ManualImportFileV1 : BaseResponse
    {
        public ManualImportFileV1(string path, long sizeBytes, int? season, int? episode, string? qualityKey, string? targetName, string? languages = null, bool isHandled = false,
            string? unplacedReason = null, long? wantedItemId = null, MediaType mediaTypeId = MediaType.Movie, MediaSubtype? mediaSubtypeId = null, string? workTitle = null, string? releaseGroup = null,
            string? problemKey = null)
        {
            WantedItemId = wantedItemId;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            WorkTitle = workTitle;
            ReleaseGroup = releaseGroup;
            ProblemKey = problemKey;
            Languages = languages;
            IsHandled = isHandled;
            UnplacedReason = unplacedReason;
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

        // Of a waiting download: a person imported this file already, so it is not chosen again.
        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.IsHandled))]
        public bool IsHandled { get; }

        // Of a waiting download: why the import did not place it (a text key below acquisition.file.), so a person decides before it is chosen.
        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.UnplacedReason))]
        public string? UnplacedReason { get; }

        // The film, series or book the file was recognised as; none when no title of the library fits or several do.
        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id), false)]
        public long? WantedItemId { get; }

        // Its title's kind, or of a file without a title what its name looks like: an episode, a film or a book.
        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        // The title with its year.
        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.WorkTitle))]
        public string? WorkTitle { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ReleaseGroup))]
        public string? ReleaseGroup { get; }

        // What a person must check (a text key below manualImport.problem.): no title or several fit, no episode, volume or chapter, the library holds it already.
        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.ProblemKey))]
        public string? ProblemKey { get; }
    }

    public sealed class ManualImportFolderV1 : BaseResponse
    {
        public ManualImportFolderV1(BaseDataList<ManualImportFileV1> files, bool isReadOnly = false)
        {
            Files = files;
            IsReadOnly = isReadOnly;
        }

        public BaseDataList<ManualImportFileV1> Files { get; }

        // An import moves its files, which a read-only connection does not allow.
        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.IsReadOnly))]
        public bool IsReadOnly { get; }
    }

    // What a person decided for one file.
    public sealed class ManualImportChoiceV1 : BaseData
    {
        public ManualImportChoiceV1(string path, bool imports, int? season, int? episode, long? wantedItemId = null, string? qualityKey = null, string[]? audioLanguages = null,
            string[]? subtitleLanguages = null)
        {
            AudioLanguages = audioLanguages ?? [];
            SubtitleLanguages = subtitleLanguages ?? [];
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

        // Another title than the one the import is for; a download keeps its own.
        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id), false)]
        public long? WantedItemId { get; }

        // The quality the person says the file has; without one its name decides.
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.QualityKey), false)]
        public string? QualityKey { get; }

        // The languages the person says the file has (three-letter codes); the scan after the import keeps them. None leaves it to the streams.
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ChosenLanguages))]
        public string[] AudioLanguages { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ChosenLanguages))]
        public string[] SubtitleLanguages { get; }
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
        public ManualImportNameV1(string path, long wantedItemId, MediaType mediaTypeId, string workTitle, string? targetName, MediaSubtype? mediaSubtypeId = null)
        {
            Path = path;
            WantedItemId = wantedItemId;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            WorkTitle = workTitle;
            TargetName = targetName;
        }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.FilePath))]
        public string Path { get; }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string WorkTitle { get; }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.TargetName))]
        public string? TargetName { get; }
    }

    // The fields of the match dialog: the film or series, the numbers and the quality a person sets for one file, and the words to search them by.
    public sealed class ManualImportMatchV1 : BaseData
    {
        public ManualImportMatchV1(string? matchQuery, long? matchedWantedItemId, int? matchedSeason, int? matchedEpisode, string? matchedQualityKey, string[]? matchedAudioLanguages = null,
            string[]? matchedSubtitleLanguages = null)
        {
            MatchedAudioLanguages = matchedAudioLanguages ?? [];
            MatchedSubtitleLanguages = matchedSubtitleLanguages ?? [];
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

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ChosenLanguages))]
        public string[] MatchedAudioLanguages { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ChosenLanguages))]
        public string[] MatchedSubtitleLanguages { get; }
    }

    // The chosen files of a folder, each with its own film, series or book; every import moves its files.
    public sealed class RunManualImportDataV1 : BaseData
    {
        public RunManualImportDataV1(long storageMountId, string relativePath, BaseDataList<ManualImportChoiceV1> files)
        {
            StorageMountId = storageMountId;
            RelativePath = relativePath;
            Files = files;
        }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.RelativePath))]
        public string RelativePath { get; }

        public BaseDataList<ManualImportChoiceV1> Files { get; }
    }

    // A waiting download's finished files with what their names say, and the folder they lie in.
    public sealed class ManualImportDownloadV1 : BaseResponse
    {
        public ManualImportDownloadV1(long wantedItemId, BaseDataList<ManualImportFileV1> files, string? folder = null, bool isReadOnly = false)
        {
            WantedItemId = wantedItemId;
            Files = files;
            Folder = folder;
            IsReadOnly = isReadOnly;
        }

        [DataField(typeof(WantedItemFields), nameof(WantedItemFields.Id))]
        public long WantedItemId { get; }

        public BaseDataList<ManualImportFileV1> Files { get; }

        // The storage connection's name and the folder inside it, or the server's folder of Jularr's own downloader.
        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.Folder))]
        public string? Folder { get; }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.IsReadOnly))]
        public bool IsReadOnly { get; }
    }

    public sealed class RunManualImportDownloadDataV1 : BaseData
    {
        public RunManualImportDownloadDataV1(BaseDataList<ManualImportChoiceV1> files, bool finishesDownload = false)
        {
            Files = files;
            FinishesDownload = finishesDownload;
        }

        // The person ends the download now, whatever else its folder still holds; without it the download waits until every video is imported.
        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.FinishesDownload))]
        public bool FinishesDownload { get; }

        public BaseDataList<ManualImportChoiceV1> Files { get; }
    }

    // What became of one chosen file: where it was moved to in the library, or the reason a person must settle.
    public sealed class ManualImportOutcomeV1 : BaseResponse
    {
        public ManualImportOutcomeV1(string path, string workTitle, string? failureKey, string[]? audioLanguages = null, string[]? subtitleLanguages = null, string? targetPath = null,
            bool sourceKept = false)
        {
            TargetPath = targetPath;
            SourceKept = sourceKept;
            AudioLanguages = audioLanguages ?? [];
            SubtitleLanguages = subtitleLanguages ?? [];
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

        // The library storage's name and the file's path inside it.
        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.TargetPath))]
        public string? TargetPath { get; }

        // The library holds the file, but its source could not be deleted (a read-only folder): it was copied.
        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.SourceKept))]
        public bool SourceKept { get; }

        // The languages a person chose for the file, as stored with it.
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ChosenLanguages))]
        public string[] AudioLanguages { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ChosenLanguages))]
        public string[] SubtitleLanguages { get; }
    }

    public sealed class ManualImportResultV1 : BaseResponse
    {
        public ManualImportResultV1(int imported, BaseDataList<ManualImportOutcomeV1>? files = null, bool finishedDownload = false)
        {
            FinishedDownload = finishedDownload;
            Imported = imported;
            Files = files ?? new([]);
        }

        public BaseDataList<ManualImportOutcomeV1> Files { get; }

        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.ImportedCount))]
        public int Imported { get; }

        // A waiting download left the queue; otherwise it keeps waiting for the files nobody imported yet.
        [DataField(typeof(ManualImportFields), nameof(ManualImportFields.FinishedDownload))]
        public bool FinishedDownload { get; }
    }
}
