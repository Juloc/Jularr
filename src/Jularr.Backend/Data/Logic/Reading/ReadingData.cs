using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Playback;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Fields.Reading;
using Jularr.Backend.Data.Fields.Storage;

namespace Jularr.Backend.Data.Logic.Reading
{
    // The readable file of an Ebook asset and where it lives.
    internal sealed class ReadingSourceData : BaseData
    {
        public ReadingSourceData(long workId, string title, long storedFileId, string relativePath, long sizeBytes, DateTime modifiedAt, bool isMissing, string rootRelativePath,
            long storageMountId, long? documentId, bool isCached)
        {
            WorkId = workId;
            Title = title;
            StoredFileId = storedFileId;
            RelativePath = relativePath;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
            IsMissing = isMissing;
            RootRelativePath = rootRelativePath;
            StorageMountId = storageMountId;
            DocumentId = documentId;
            IsCached = isCached;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.IsMissing))]
        public bool IsMissing { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RootRelativePath { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.Id))]
        public long StorageMountId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId), false)]
        public long? DocumentId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.IsCached))]
        public bool IsCached { get; }
    }

    internal sealed class ReadingSourceLookupData : BaseData
    {
        public ReadingSourceLookupData(long mediaAssetId, int analyzerVersion, int comicAnalyzerVersion, int pdfAnalyzerVersion)
        {
            PdfAnalyzerVersion = pdfAnalyzerVersion;
            MediaAssetId = mediaAssetId;
            AnalyzerVersion = analyzerVersion;
            ComicAnalyzerVersion = comicAnalyzerVersion;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.AnalyzerVersion))]
        public int AnalyzerVersion { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.AnalyzerVersion))]
        public int ComicAnalyzerVersion { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.AnalyzerVersion))]
        public int PdfAnalyzerVersion { get; }
    }

    // The analyzer versions a prepared document must have to count as current, for the books the background preparation looks for.
    internal sealed class ReadingPreparationLookupData : BaseData
    {
        public ReadingPreparationLookupData(int analyzerVersion, int pdfAnalyzerVersion)
        {
            AnalyzerVersion = analyzerVersion;
            PdfAnalyzerVersion = pdfAnalyzerVersion;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.AnalyzerVersion))]
        public int AnalyzerVersion { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.AnalyzerVersion))]
        public int PdfAnalyzerVersion { get; }
    }

    internal sealed class ReadingOpenedData : BaseData
    {
        public ReadingOpenedData(long documentId, long mediaAssetId, long profileId)
        {
            DocumentId = documentId;
            MediaAssetId = mediaAssetId;
            ProfileId = profileId;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }
    }

    internal sealed class ReadingDocumentSaveData : BaseData
    {
        public ReadingDocumentSaveData(long storedFileId, long sourceSizeBytes, DateTime sourceModifiedAt, byte[] contentHash, int analyzerVersion, ReadingFormatType readingFormatTypeId,
            string? title, string? language, string structure, long totalCharacters)
        {
            StoredFileId = storedFileId;
            SourceSizeBytes = sourceSizeBytes;
            SourceModifiedAt = sourceModifiedAt;
            ContentHash = contentHash;
            AnalyzerVersion = analyzerVersion;
            ReadingFormatTypeId = readingFormatTypeId;
            Title = title;
            Language = language;
            Structure = structure;
            TotalCharacters = totalCharacters;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SizeBytes))]
        public long SourceSizeBytes { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.ModifiedAt))]
        public DateTime SourceModifiedAt { get; }

        [JsonIgnore]
        [DataField(typeof(ReadingFields), nameof(ReadingFields.ContentHash))]
        public byte[] ContentHash { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.AnalyzerVersion))]
        public int AnalyzerVersion { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.ReadingFormatTypeId))]
        public ReadingFormatType ReadingFormatTypeId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Title))]
        public string? Title { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Language))]
        public string? Language { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Structure))]
        public string Structure { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.TotalCharacters))]
        public long TotalCharacters { get; }
    }

    internal sealed class ReadingDocumentStateData : BaseData
    {
        public ReadingDocumentStateData(long documentId, bool isCached)
        {
            DocumentId = documentId;
            IsCached = isCached;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.IsCached))]
        public bool IsCached { get; }
    }

    internal sealed class ReadingDocumentCompleteData : BaseData
    {
        public ReadingDocumentCompleteData(long documentId, string? title, string? language, string structure, long totalCharacters)
        {
            DocumentId = documentId;
            Title = title;
            Language = language;
            Structure = structure;
            TotalCharacters = totalCharacters;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Title))]
        public string? Title { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Language))]
        public string? Language { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Structure))]
        public string Structure { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.TotalCharacters))]
        public long TotalCharacters { get; }
    }

    internal sealed class ReadingDocumentIdData : BaseData
    {
        public ReadingDocumentIdData(long documentId)
        {
            DocumentId = documentId;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }
    }

    internal sealed class ReadingTranslationPathData : BaseData
    {
        public ReadingTranslationPathData(long documentId, string path)
        {
            DocumentId = documentId;
            Path = path;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Path))]
        public string Path { get; }
    }

    // A prepared book the profile may read, for its EPUB.
    internal sealed class ReadingEbookDocumentData : BaseData
    {
        public ReadingEbookDocumentData(long documentId, string title, string structure, string? language, DateTime cacheCompletedAt, bool isRightToLeft)
        {
            DocumentId = documentId;
            Title = title;
            Structure = structure;
            Language = language;
            CacheCompletedAt = cacheCompletedAt;
            IsRightToLeft = isRightToLeft;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Structure))]
        public string Structure { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Language))]
        public string? Language { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.CacheCompletedAt))]
        public DateTime CacheCompletedAt { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.IsRightToLeft))]
        public bool IsRightToLeft { get; }
    }

    internal sealed class ReadingEbookResourceData : BaseData
    {
        public ReadingEbookResourceData(string path, string mediaType, byte[] bytes)
        {
            Path = path;
            MediaType = mediaType;
            Bytes = bytes;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Path))]
        public string Path { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.MediaType))]
        public string MediaType { get; }

        [JsonIgnore]
        [DataField(typeof(ReadingFields), nameof(ReadingFields.Bytes))]
        public byte[] Bytes { get; }
    }

    // A scanned PDF to recognize, as long as its file stays the same.
    internal sealed class ReadingRecognitionData : BaseData
    {
        public ReadingRecognitionData(long storedFileId, long sizeBytes, DateTime modifiedAt)
        {
            StoredFileId = storedFileId;
            SizeBytes = sizeBytes;
            ModifiedAt = modifiedAt;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SizeBytes))]
        public long SizeBytes { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.ModifiedAt))]
        public DateTime ModifiedAt { get; }
    }

    internal sealed class ReadingRecognitionStateData : BaseData
    {
        public ReadingRecognitionStateData(bool isPending)
        {
            IsPending = isPending;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.IsPending))]
        public bool IsPending { get; }
    }

    // The next queued scan, the language its book was written in, how many pages it has once known, and the page a reader has open.
    internal sealed class ReadingRecognitionQueueData : BaseData
    {
        public ReadingRecognitionQueueData(long storedFileId, string? language, int? pages, int? wantedPage)
        {
            StoredFileId = storedFileId;
            Language = language;
            Pages = pages;
            WantedPage = wantedPage;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.OriginalLanguage))]
        public string? Language { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Pages))]
        public int? Pages { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNumber), false)]
        public int? WantedPage { get; }
    }

    // What one page of a scan was found to hold: recognized lines as fractions of the page, and how plausible their text is.
    internal sealed class ReadingRecognizedPageData : BaseData
    {
        public ReadingRecognizedPageData(long storedFileId, int pageNumber, string reason, decimal quality, string lines)
        {
            StoredFileId = storedFileId;
            PageNumber = pageNumber;
            Reason = reason;
            Quality = quality;
            Lines = lines;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNumber))]
        public int PageNumber { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.RecognizedReason))]
        public string Reason { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Quality))]
        public decimal Quality { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.RecognizedLines))]
        public string Lines { get; }
    }

    internal sealed class ReadingRecognizedPageNumberData : BaseData
    {
        public ReadingRecognizedPageNumberData(int pageNumber)
        {
            PageNumber = pageNumber;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNumber))]
        public int PageNumber { get; }
    }

    // The pages a scan has, once its first pass has counted them.
    internal sealed class ReadingRecognitionPagesData : BaseData
    {
        public ReadingRecognitionPagesData(long storedFileId, int pages)
        {
            StoredFileId = storedFileId;
            Pages = pages;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Pages), true)]
        public int Pages { get; }
    }

    // The page a profile has open in a book's PDF; its recognition reads that region first.
    internal sealed class ReadingWantedPageData : BaseData
    {
        public ReadingWantedPageData(long mediaAssetId, int pageNumber)
        {
            MediaAssetId = mediaAssetId;
            PageNumber = pageNumber;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNumber))]
        public int PageNumber { get; }
    }

    internal sealed class ReadingRecognitionResultData : BaseData
    {
        public ReadingRecognitionResultData(long storedFileId, bool isFailed)
        {
            StoredFileId = storedFileId;
            IsFailed = isFailed;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.IsMissing))]
        public bool IsFailed { get; }
    }

    // How many blocks a prepared section has.
    internal sealed class ReadingSectionBlocksData : BaseData
    {
        public ReadingSectionBlocksData(int blocks)
        {
            Blocks = blocks;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionCount))]
        public int Blocks { get; }
    }

    internal sealed class ReadingResourceData : BaseData
    {
        public ReadingResourceData(long documentId, string path, string mediaType, byte[] bytes)
        {
            DocumentId = documentId;
            Path = path;
            MediaType = mediaType;
            Bytes = bytes;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Path))]
        public string Path { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.MediaType))]
        public string MediaType { get; }

        [JsonIgnore]
        [DataField(typeof(ReadingFields), nameof(ReadingFields.Bytes))]
        public byte[] Bytes { get; }
    }

    internal sealed class ReadingCacheBudgetData : BaseData
    {
        public ReadingCacheBudgetData(long cacheBudgetBytes, int cacheBudgetWorks, DateTime protectedSince)
        {
            CacheBudgetBytes = cacheBudgetBytes;
            CacheBudgetWorks = cacheBudgetWorks;
            ProtectedSince = protectedSince;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.CacheBudgetBytes))]
        public long CacheBudgetBytes { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.CacheBudgetWorks))]
        public int CacheBudgetWorks { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.ProtectedSince))]
        public DateTime ProtectedSince { get; }
    }

    internal sealed class ReadingEvictionData : BaseData
    {
        public ReadingEvictionData(int count)
        {
            Count = count;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Count))]
        public int Count { get; }
    }

    internal sealed class ReadingProgressData : BaseData
    {
        public ReadingProgressData(long profileId, long mediaAssetId, string locator, decimal progression, bool isCompleted)
        {
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            Locator = locator;
            Progression = progression;
            IsCompleted = isCompleted;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Locator), true)]
        public string Locator { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Progression))]
        public decimal Progression { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.IsCompleted))]
        public bool IsCompleted { get; }
    }

    // Spine sections and table of contents, stored as JSON.
    internal sealed class ReadingStructure
    {
        public ReadingStructure(ReadingStructureSection[] sections, ReadingStructureEntry[] toc, bool recommended = true, string? reason = null, int[]? originalPages = null)
        {
            Sections = sections;
            Toc = toc;
            Recommended = recommended;
            Reason = reason;
            OriginalPages = originalPages ?? [];
        }

        public ReadingStructureSection[] Sections { get; }
        public ReadingStructureEntry[] Toc { get; }

        // Whether the reading text is good enough to open by itself (a converted PDF's quality gate), and if not, why; always for other books.
        public bool Recommended { get; }
        public string? Reason { get; }

        // The pages of a converted PDF that stand in the text as the original page and are not part of a downloaded EPUB.
        public int[] OriginalPages { get; }
    }

    internal sealed class ReadingStructureSection
    {
        public ReadingStructureSection(string path, string? title, long characters, int blocks, int pageFrom = 0, int pageTo = 0)
        {
            Path = path;
            Title = title;
            Characters = characters;
            Blocks = blocks;
            PageFrom = pageFrom;
            PageTo = pageTo;
        }

        public string Path { get; }
        public string? Title { get; }
        public long Characters { get; }
        public int Blocks { get; }

        // The first and last page of the PDF the section covers; 0 for books that are no PDF.
        public int PageFrom { get; }
        public int PageTo { get; }
    }

    internal sealed class ReadingStructureEntry
    {
        public ReadingStructureEntry(string title, int section, int level, string? anchor)
        {
            Title = title;
            Section = section;
            Level = level;
            Anchor = anchor;
        }

        public string Title { get; }
        public int Section { get; }
        public int Level { get; }
        public string? Anchor { get; }
    }

    internal sealed class EpubItem
    {
        public EpubItem(string path, string mediaType, string[] properties)
        {
            Path = path;
            MediaType = mediaType;
            Properties = properties;
        }

        public string Path { get; }
        public string MediaType { get; }
        public string[] Properties { get; }
    }

    internal sealed class EpubTocItem
    {
        public EpubTocItem(string title, string path, string? anchor, int level)
        {
            Title = title;
            Path = path;
            Anchor = anchor;
            Level = level;
        }

        public string Title { get; }
        public string Path { get; }
        public string? Anchor { get; }
        public int Level { get; }
    }

    internal sealed class EpubPackage
    {
        public EpubPackage(string? title, string? language, IReadOnlyList<EpubItem> spine, IReadOnlyList<EpubTocItem> toc, IReadOnlyDictionary<string, string> manifest)
        {
            Title = title;
            Language = language;
            Spine = spine;
            Toc = toc;
            Manifest = manifest;
        }

        public string? Title { get; }
        public string? Language { get; }
        public IReadOnlyList<EpubItem> Spine { get; }
        public IReadOnlyList<EpubTocItem> Toc { get; }

        // Archive path to media type.
        public IReadOnlyDictionary<string, string> Manifest { get; }
    }

    internal sealed class PreparedSection
    {
        public PreparedSection(string html, long characters, int blocks, IReadOnlyCollection<string> resources)
        {
            Html = html;
            Characters = characters;
            Blocks = blocks;
            Resources = resources;
        }

        public string Html { get; }
        public long Characters { get; }
        public int Blocks { get; }

        // Archive paths of images the section shows.
        public IReadOnlyCollection<string> Resources { get; }
    }

    internal sealed class ReadingSectionTextData : BaseData
    {
        public ReadingSectionTextData(int sectionIndex, string? title, string html)
        {
            SectionIndex = sectionIndex;
            Title = title;
            Html = html;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Title))]
        public string? Title { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Html))]
        public string Html { get; }
    }

    // The prepared document a book's PDF pages map into: its identity and its structure.
    internal sealed class ReadingStructureTextData : BaseData
    {
        public ReadingStructureTextData(long documentId, string structure)
        {
            DocumentId = documentId;
            Structure = structure;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Structure))]
        public string Structure { get; }
    }

    internal sealed class ReadingViewData : BaseData
    {
        public ReadingViewData(long profileId, long mediaAssetId, ReadingView? readingViewTypeId)
        {
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            ReadingViewTypeId = readingViewTypeId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.ReadingViewTypeId), false)]
        public ReadingView? ReadingViewTypeId { get; }
    }

    internal sealed class ReadingLanguageData : BaseData
    {
        public ReadingLanguageData(long profileId, long mediaAssetId, string? readingLanguage)
        {
            ProfileId = profileId;
            MediaAssetId = mediaAssetId;
            ReadingLanguage = readingLanguage;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.ReadingLanguage))]
        public string? ReadingLanguage { get; }
    }
}
