using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Library;

namespace Jularr.Backend.Data.Admin.Library.V1
{
    public sealed class LibraryRenamesParametersV1 : BaseParameters
    {
    }

    public sealed class ReadLibraryRenamesParametersV1 : BaseParameters
    {
        public ReadLibraryRenamesParametersV1(int page = 1, int pageSize = 100)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
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
        public LibraryRenamePlanV1(BaseDataList<LibraryRenameV1> items, string fingerprint)
        {
            Items = items;
            Fingerprint = fingerprint;
        }

        public BaseDataList<LibraryRenameV1> Items { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RenameFingerprint))]
        public string Fingerprint { get; }
    }

    public sealed class ApplyLibraryRenamesDataV1 : BaseData
    {
        public ApplyLibraryRenamesDataV1(string fingerprint)
        {
            Fingerprint = fingerprint;
        }

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

    public sealed class NamingPatternV1 : BaseResponse
    {
        public NamingPatternV1(MediaType mediaTypeId, string? folder, string file, string example, long version)
        {
            MediaTypeId = mediaTypeId;
            Folder = folder;
            File = file;
            Example = example;
            Version = version;
        }

        [DataField(typeof(NamingPatternFields), nameof(NamingPatternFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

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
}
