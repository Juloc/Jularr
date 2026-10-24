using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class DownloadClientFolderRequestV1 : BaseData
    {
        public DownloadClientFolderRequestV1(string area, string? relativePath = null, int pageNumber = 1, bool confirmed = false, string? fingerprint = null)
        {
            Area = area;
            RelativePath = relativePath;
            PageNumber = pageNumber;
            Confirmed = confirmed;
            Fingerprint = fingerprint;
        }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Folder))]
        public string Area { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Path))]
        public string? RelativePath { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Page))]
        public int PageNumber { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Flag))]
        public bool Confirmed { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Fingerprint))]
        public string? Fingerprint { get; }
    }

    public sealed class DownloadClientFileV1 : BaseResponse
    {
        public DownloadClientFileV1(string name, string relativePath, bool isDirectory, long? sizeBytes, string assignment)
        {
            Name = name;
            RelativePath = relativePath;
            IsDirectory = isDirectory;
            SizeBytes = sizeBytes;
            Assignment = assignment;
        }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.FileName))]
        public string Name { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Path))]
        public string RelativePath { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Flag))]
        public bool IsDirectory { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Bytes))]
        public long? SizeBytes { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Assignment))]
        public string Assignment { get; }
    }

    public sealed class DownloadClientBrowseResultV1 : BaseResponse
    {
        public DownloadClientBrowseResultV1(string area, string root, string relativePath, bool hasMore, BaseDataList<DownloadClientFileV1> entries)
        {
            Area = area;
            Root = root;
            RelativePath = relativePath;
            HasMore = hasMore;
            Entries = entries;
        }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Folder))]
        public string Area { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Path))]
        public string Root { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Path))]
        public string RelativePath { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Flag))]
        public bool HasMore { get; }
        public BaseDataList<DownloadClientFileV1> Entries { get; }
    }

    public sealed class DownloadClientDeletionPreviewV1 : BaseResponse
    {
        public DownloadClientDeletionPreviewV1(string area, string relativePath, int files, long bytes, string fingerprint)
        {
            Area = area;
            RelativePath = relativePath;
            Files = files;
            Bytes = bytes;
            Fingerprint = fingerprint;
        }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Folder))]
        public string Area { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Path))]
        public string RelativePath { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Page))]
        public int Files { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Bytes))]
        public long Bytes { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Fingerprint))]
        public string Fingerprint { get; }
    }
}
