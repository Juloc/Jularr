using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Enums.Storage;
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
            long storageMountId, StorageProtocolType storageProtocolTypeId, string? sharePath, long? documentId, bool isCached)
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
            StorageProtocolTypeId = storageProtocolTypeId;
            SharePath = sharePath;
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

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.StorageProtocolTypeId))]
        public StorageProtocolType StorageProtocolTypeId { get; }

        [DataField(typeof(StorageMountFields), nameof(StorageMountFields.SharePath))]
        public string? SharePath { get; }

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
        public ReadingStructure(ReadingStructureSection[] sections, ReadingStructureEntry[] toc)
        {
            Sections = sections;
            Toc = toc;
        }

        public ReadingStructureSection[] Sections { get; }
        public ReadingStructureEntry[] Toc { get; }
    }

    internal sealed class ReadingStructureSection
    {
        public ReadingStructureSection(string path, string? title, long characters, int blocks)
        {
            Path = path;
            Title = title;
            Characters = characters;
            Blocks = blocks;
        }

        public string Path { get; }
        public string? Title { get; }
        public long Characters { get; }
        public int Blocks { get; }
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
}
