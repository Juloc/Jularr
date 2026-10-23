using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Library.V1
{
    public sealed class LibraryRenamesParametersV1 : BaseParameters
    {
    }

    public sealed class ReadLibraryRenamesParametersV1 : BaseParameters
    {
        public ReadLibraryRenamesParametersV1(int page = 1, int pageSize = 100, long? workId = null)
        {
            Page = page;
            PageSize = pageSize;
            WorkId = workId;
        }

        public int Page { get; }
        public int PageSize { get; }

        // Only the renames of this title's files.
        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }
    }

    public sealed class LibraryRenameV1 : BaseResponse
    {
        public LibraryRenameV1(long storedFileId, string libraryRootName, string relativePath, string targetPath)
        {
            StoredFileId = storedFileId;
            LibraryRootName = libraryRootName;
            RelativePath = relativePath;
            TargetPath = targetPath;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Name))]
        public string LibraryRootName { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.TargetPath))]
        public string TargetPath { get; }
    }

    // One page of the whole plan; applying needs the fingerprint of exactly this plan.
    public sealed class LibraryRenamePlanV1 : BaseResponse
    {
        public LibraryRenamePlanV1(BaseDataList<LibraryRenameV1> items, string fingerprint, long total = 0)
        {
            Items = items;
            Fingerprint = fingerprint;
            Total = total;
        }

        // How many files the whole plan renames; one page of them is listed.
        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.FileCount))]
        public long Total { get; }

        public BaseDataList<LibraryRenameV1> Items { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RenameFingerprint))]
        public string Fingerprint { get; }
    }

    public sealed class ApplyLibraryRenamesDataV1 : BaseData
    {
        public ApplyLibraryRenamesDataV1(string fingerprint, long? workId = null)
        {
            Fingerprint = fingerprint;
            WorkId = workId;
        }

        // Only the renames of this title's files; the fingerprint is that of this part.
        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RenameFingerprint))]
        public string Fingerprint { get; }
    }

    // Skipped files stay where they are: storage offline, a name taken meanwhile or the file gone.
    public sealed class ApplyLibraryRenamesResultV1 : BaseResponse
    {
        public ApplyLibraryRenamesResultV1(int renamedCount, int skippedCount)
        {
            RenamedCount = renamedCount;
            SkippedCount = skippedCount;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RenamedCount))]
        public int RenamedCount { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SkippedCount))]
        public int SkippedCount { get; }
    }

    public sealed class ReadNamingPatternsParametersV1 : BaseParameters
    {
    }

    public sealed class NamingPatternParametersV1 : BaseParameters
    {
        public NamingPatternParametersV1(MediaType mediaTypeId)
        {
            MediaTypeId = mediaTypeId;
        }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }
    }

    // A subtype's own pattern is addressed by both.
    public sealed class NamingSubtypePatternParametersV1 : BaseParameters
    {
        public NamingSubtypePatternParametersV1(MediaType mediaTypeId, MediaSubtype mediaSubtypeId)
        {
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
        }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaSubtypeId))]
        public MediaSubtype MediaSubtypeId { get; }
    }

    public sealed class NamingPatternV1 : BaseResponse
    {
        public NamingPatternV1(MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string? folder, string file, string example, long version)
        {
            MediaSubtypeId = mediaSubtypeId;
            MediaTypeId = mediaTypeId;
            Folder = folder;
            File = file;
            Example = example;
            Version = version;
        }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Folder))]
        public string? Folder { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.File))]
        public string File { get; }

        // A sample title in this pattern.
        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Example))]
        public string Example { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Version))]
        public long Version { get; }
    }

    // The pattern as typed, before it is saved.
    public sealed class NamingPreviewParametersV1 : BaseParameters
    {
        public NamingPreviewParametersV1(MediaType mediaTypeId, string? folder, string file)
        {
            MediaTypeId = mediaTypeId;
            Folder = folder;
            File = file;
        }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Folder))]
        public string? Folder { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.File))]
        public string File { get; }
    }

    // A sample title in the typed pattern, or why it cannot be used.
    public sealed class NamingPreviewV1 : BaseResponse
    {
        public NamingPreviewV1(string? example, string? problem)
        {
            Example = example;
            Problem = problem;
        }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Example))]
        public string? Example { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Problem))]
        public string? Problem { get; }
    }

    public sealed class UpdateNamingPatternDataV1 : BaseData
    {
        public UpdateNamingPatternDataV1(string? folder, string file, long version)
        {
            Folder = folder;
            File = file;
            Version = version;
        }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Folder))]
        public string? Folder { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.File))]
        public string File { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Version))]
        public long Version { get; }
    }

    public sealed class ReadNamingSettingsParametersV1 : BaseParameters
    {
    }

    public sealed class NamingSettingsParametersV1 : BaseParameters
    {
        public NamingSettingsParametersV1(MediaType mediaTypeId)
        {
            MediaTypeId = mediaTypeId;
        }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }
    }

    // How names are made of values for one media type.
    public sealed class NamingSettingsV1 : BaseResponse
    {
        public NamingSettingsV1(MediaType mediaTypeId, bool replacesIllegalCharacters, ColonReplacement colonReplacementId, string? customColonReplacement, string? specialsFolder, long version)
        {
            MediaTypeId = mediaTypeId;
            ReplacesIllegalCharacters = replacesIllegalCharacters;
            ColonReplacementId = colonReplacementId;
            CustomColonReplacement = customColonReplacement;
            SpecialsFolder = specialsFolder;
            Version = version;
        }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.ReplacesIllegalCharacters))]
        public bool ReplacesIllegalCharacters { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.ColonReplacementId))]
        public ColonReplacement ColonReplacementId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.CustomColonReplacement))]
        public string? CustomColonReplacement { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.SpecialsFolder))]
        public string? SpecialsFolder { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Version))]
        public long Version { get; }
    }

    public sealed class UpdateNamingSettingsDataV1 : BaseData
    {
        public UpdateNamingSettingsDataV1(bool replacesIllegalCharacters, ColonReplacement colonReplacementId, string? customColonReplacement, string? specialsFolder, long version)
        {
            ReplacesIllegalCharacters = replacesIllegalCharacters;
            ColonReplacementId = colonReplacementId;
            CustomColonReplacement = customColonReplacement;
            SpecialsFolder = specialsFolder;
            Version = version;
        }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.ReplacesIllegalCharacters))]
        public bool ReplacesIllegalCharacters { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.ColonReplacementId))]
        public ColonReplacement ColonReplacementId { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.CustomColonReplacement))]
        public string? CustomColonReplacement { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.SpecialsFolder))]
        public string? SpecialsFolder { get; }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.Version))]
        public long Version { get; }
    }
}
