using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Playback;
using Jularr.Backend.Data.Fields.Reading;

namespace Jularr.Backend.Data.User.Reading.V1
{
    public sealed class ReadingAssetParametersV1 : BaseParameters
    {
        public ReadingAssetParametersV1(long mediaAssetId)
        {
            MediaAssetId = mediaAssetId;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }
    }

    public sealed class ReadingDocumentParametersV1 : BaseParameters
    {
        public ReadingDocumentParametersV1(long documentId)
        {
            DocumentId = documentId;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }
    }

    public sealed class ReadingSectionParametersV1 : BaseParameters
    {
        public ReadingSectionParametersV1(long documentId, int sectionIndex)
        {
            DocumentId = documentId;
            SectionIndex = sectionIndex;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; }
    }

    public sealed class ReadingResourceParametersV1 : BaseParameters
    {
        public ReadingResourceParametersV1(long documentId, string path)
        {
            DocumentId = documentId;
            Path = path;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Path))]
        public string Path { get; }
    }

    public sealed class ReadingDocumentV1 : BaseResponse
    {
        public ReadingDocumentV1(long documentId, long workId, string title, ReadingFormatType readingFormatTypeId, int sectionCount, bool isCached, string? locator, decimal? progression)
        {
            DocumentId = documentId;
            WorkId = workId;
            Title = title;
            ReadingFormatTypeId = readingFormatTypeId;
            SectionCount = sectionCount;
            IsCached = isCached;
            Locator = locator;
            Progression = progression;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.ReadingFormatTypeId))]
        public ReadingFormatType ReadingFormatTypeId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionCount))]
        public int SectionCount { get; }

        // Readable without the storage.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.IsCached))]
        public bool IsCached { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Locator))]
        public string? Locator { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Progression), false)]
        public decimal? Progression { get; }
    }

    public sealed class ReadingTocEntryV1 : BaseResponse
    {
        public ReadingTocEntryV1(string title, int sectionIndex, int level, string? anchor)
        {
            Title = title;
            SectionIndex = sectionIndex;
            Level = level;
            Anchor = anchor;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Title))]
        public string Title { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Level))]
        public int Level { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Anchor))]
        public string? Anchor { get; }
    }

    public sealed class ReadingSectionV1 : BaseResponse
    {
        public ReadingSectionV1(int sectionIndex, string? title, string html, long characters, long charactersBefore, long totalCharacters, int blocks)
        {
            SectionIndex = sectionIndex;
            Title = title;
            Html = html;
            Characters = characters;
            CharactersBefore = charactersBefore;
            TotalCharacters = totalCharacters;
            Blocks = blocks;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Title))]
        public string? Title { get; }

        // Sanitized when the document was prepared.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.Html))]
        public string Html { get; }

        // Progress is measured in characters across the book.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.TotalCharacters))]
        public long Characters { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.TotalCharacters))]
        public long CharactersBefore { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.TotalCharacters))]
        public long TotalCharacters { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionCount))]
        public int Blocks { get; }
    }

    public sealed class ReadingResourceV1 : BaseResponse, IBinaryOutput
    {
        public ReadingResourceV1(byte[] bytes, string contentType)
        {
            Bytes = bytes;
            ContentType = contentType;
        }

        [JsonIgnore]
        public byte[] Bytes { get; }

        [JsonIgnore]
        public string ContentType { get; }
    }

    public sealed class SaveReadingProgressDataV1 : BaseData
    {
        public SaveReadingProgressDataV1(string locator, decimal progression, bool isCompleted)
        {
            Locator = locator;
            Progression = progression;
            IsCompleted = isCompleted;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Locator), true)]
        public string Locator { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Progression))]
        public decimal Progression { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.IsCompleted))]
        public bool IsCompleted { get; }
    }
}
