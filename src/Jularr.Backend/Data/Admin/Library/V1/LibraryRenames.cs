using Jularr.Backend.Data.Common;
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
        public LibraryRenamePlanV1(BaseDataList<LibraryRenameV1> items, long totalCount, string fingerprint)
        {
            Items = items;
            TotalCount = totalCount;
            Fingerprint = fingerprint;
        }

        public BaseDataList<LibraryRenameV1> Items { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.FileCount))]
        public long TotalCount { get; }

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
}
