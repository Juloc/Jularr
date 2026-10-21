using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Reading;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Logic.Reading
{
    // The next PDF to turn into a reading copy: its book, where it lives and the copy a former attempt left.
    internal sealed class ReadingDerivationSourceData : BaseData
    {
        public ReadingDerivationSourceData(long storedFileId, long mediaAssetId, long libraryRootId, string relativePath, long sizeBytes, DateTime modifiedAt, string rootRelativePath,
            long storageMountId, string title, string? language, long? ebookStoredFileId, string? ebookRelativePath)
        {
            StoredFileId = storedFileId;
            MediaAssetId = mediaAssetId;
            LibraryRootId = libraryRootId;
            RelativePath = relativePath;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
            RootRelativePath = rootRelativePath;
            StorageMountId = storageMountId;
            Title = title;
            Language = language;
            EbookStoredFileId = ebookStoredFileId;
            EbookRelativePath = ebookRelativePath;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RootRelativePath { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.OriginalLanguage))]
        public string? Language { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id), false)]
        public long? EbookStoredFileId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath), false)]
        public string? EbookRelativePath { get; }
    }

    // An attempt starts for this version of the PDF.
    internal sealed class ReadingDerivationStartData : BaseData
    {
        public ReadingDerivationStartData(long storedFileId, long sizeBytes, DateTime modifiedAt, int analyzerVersion)
        {
            StoredFileId = storedFileId;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
            AnalyzerVersion = analyzerVersion;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.AnalyzerVersion))]
        public int AnalyzerVersion { get; }
    }

    // How an attempt ended; the copy's file belongs to the result only when it is ready.
    internal sealed class ReadingDerivationResultData : BaseData
    {
        public ReadingDerivationResultData(long storedFileId, long sizeBytes, DateTime modifiedAt, byte[]? sha256, int analyzerVersion, ReadingDerivationStatus status, string? reason, int? pages,
            int? chapters, int? figures, long? ebookStoredFileId)
        {
            StoredFileId = storedFileId;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
            Sha256 = sha256;
            AnalyzerVersion = analyzerVersion;
            ReadingDerivationStatusTypeId = status;
            Reason = reason;
            Pages = pages;
            Chapters = chapters;
            Figures = figures;
            EbookStoredFileId = ebookStoredFileId;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Sha256))]
        public byte[]? Sha256 { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.AnalyzerVersion))]
        public int AnalyzerVersion { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.ReadingDerivationStatusTypeId))]
        public ReadingDerivationStatus ReadingDerivationStatusTypeId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Reason))]
        public string? Reason { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Pages))]
        public int? Pages { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Chapters))]
        public int? Chapters { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Figures))]
        public int? Figures { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id), false)]
        public long? EbookStoredFileId { get; }
    }

    // The version of the conversion a copy must have to count as current.
    internal sealed class ReadingAnalyzerData : BaseData
    {
        public ReadingAnalyzerData(int analyzerVersion)
        {
            AnalyzerVersion = analyzerVersion;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.AnalyzerVersion))]
        public int AnalyzerVersion { get; }
    }

    // The written copy as a file of its book's library.
    internal sealed class ReadingDerivedFileData : BaseData
    {
        public ReadingDerivedFileData(long libraryRootId, string relativePath, long sizeBytes, DateTime modifiedAt, long mediaAssetId)
        {
            LibraryRootId = libraryRootId;
            RelativePath = relativePath;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
            MediaAssetId = mediaAssetId;
        }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id))]
        public long LibraryRootId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }
    }

    // What a book's PDF is doing on its way to a reading copy, for the reader's status line.
    internal sealed class ReadingDerivationStateData : BaseData
    {
        public ReadingDerivationStateData(ReadingDerivationStatus? status, string? reason)
        {
            ReadingDerivationStatusTypeId = status;
            Reason = reason;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.ReadingDerivationStatusTypeIdOptional))]
        public ReadingDerivationStatus? ReadingDerivationStatusTypeId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Reason))]
        public string? Reason { get; }
    }
}
