using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Translation;
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

    public sealed class ReadingTranslationParametersV1 : BaseParameters
    {
        public ReadingTranslationParametersV1(long documentId, int sectionIndex, string language)
        {
            DocumentId = documentId;
            SectionIndex = sectionIndex;
            Language = language;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string Language { get; }
    }

    // A section's blocks in another language: a JSON array of plain texts in block order.
    public sealed class ReadingTranslationV1 : BaseResponse
    {
        public ReadingTranslationV1(string blocks)
        {
            Blocks = blocks;
        }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Blocks))]
        public string Blocks { get; }
    }

    public sealed class SaveReadingTranslationDataV1 : BaseData
    {
        public SaveReadingTranslationDataV1(string blocks)
        {
            Blocks = blocks;
        }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Blocks))]
        public string Blocks { get; }
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
        public ReadingDocumentV1(long documentId, long workId, string title, ReadingFormatType readingFormatTypeId, int sectionCount, bool isCached, string? locator, decimal? progression,
            bool isRightToLeft, string? language, string? readingLanguage = null, string? originalLanguage = null, string[]? translatedLanguages = null, bool isRecommended = true, int originalPages = 0)
        {
            IsRecommended = isRecommended;
            OriginalPages = originalPages;
            TranslatedLanguages = translatedLanguages ?? [];
            ReadingLanguage = readingLanguage;
            OriginalLanguage = originalLanguage;
            Language = language;
            DocumentId = documentId;
            WorkId = workId;
            Title = title;
            ReadingFormatTypeId = readingFormatTypeId;
            SectionCount = sectionCount;
            IsCached = isCached;
            Locator = locator;
            Progression = progression;
            IsRightToLeft = isRightToLeft;
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

        // Manga read right to left; comic pages are the sections.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.IsRightToLeft))]
        public bool IsRightToLeft { get; }

        // The book's own language tag, where it names one.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.Language))]
        public string? Language { get; }

        // The profile's choice for this book, and the language the book was written in.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.ReadingLanguage))]
        public string? ReadingLanguage { get; }

        [DataField(typeof(Fields.Media.WorkFields), nameof(Fields.Media.WorkFields.OriginalLanguage))]
        public string? OriginalLanguage { get; }

        // Languages every section has a shared translation in, so the book downloads translated.
        [DataField(typeof(TranslationFields), nameof(TranslationFields.Languages))]
        public string[] TranslatedLanguages { get; }

        // The reading text of a PDF passed its quality gate and opens by itself; false where the original pages read better. Every other book is.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.IsRecommended))]
        public bool IsRecommended { get; }

        // Pages of the PDF that stand in the text as the original page, and so are not in a downloaded EPUB.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.Count))]
        public int OriginalPages { get; }
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
        public ReadingSectionV1(int sectionIndex, string? title, string html, long characters, long charactersBefore, long totalCharacters, int blocks, int pageFrom = 0)
        {
            PageFrom = pageFrom;
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

        // The first page of the PDF the section covers; 0 for a book that is no PDF.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionCount))]
        public int PageFrom { get; }
    }

    public sealed class ReadingEbookParametersV1 : BaseParameters
    {
        public ReadingEbookParametersV1(long documentId, string? language)
        {
            DocumentId = documentId;
            Language = language;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        // Empty is the book as prepared; a language takes its finished translation.
        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language), false)]
        public string? Language { get; }
    }

    public sealed class ReadingEbookV1 : BaseResponse, IDownloadOutput
    {
        public ReadingEbookV1(byte[] bytes, string fileName)
        {
            Bytes = bytes;
            FileName = fileName;
        }

        [JsonIgnore]
        public byte[] Bytes { get; }

        [JsonIgnore]
        public string FileName { get; }

        [JsonIgnore]
        public string ContentType => "application/epub+zip";
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

    public sealed class SaveReadingLanguageDataV1 : BaseData
    {
        public SaveReadingLanguageDataV1(string? readingLanguage)
        {
            ReadingLanguage = readingLanguage;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.ReadingLanguage))]
        public string? ReadingLanguage { get; }
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

    public sealed class ReadingSearchParametersV1 : BaseParameters
    {
        public ReadingSearchParametersV1(long documentId, string query)
        {
            DocumentId = documentId;
            Query = query;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Query))]
        public string Query { get; }
    }

    // A section containing the words, with the text around the first match.
    public sealed class ReadingSearchHitV1 : BaseResponse
    {
        public ReadingSearchHitV1(int sectionIndex, string? title, string snippet, int matches, int? page = null)
        {
            SectionIndex = sectionIndex;
            Title = title;
            Snippet = snippet;
            Matches = matches;
            Page = page;
        }

        // The page of the PDF the first match stands on, for a converted PDF.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNumber), false)]
        public int? Page { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Title))]
        public string? Title { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Snippet))]
        public string Snippet { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Count))]
        public int Matches { get; }
    }

    // How the book opens: prepared text or pages, else the original pages of its PDF.
    public sealed class ReadingSourceV1 : BaseResponse
    {
        public ReadingSourceV1(long workId, string title, ReadingFormatType readingFormatTypeId, bool hasPdf, ReadingDerivationStatus? readingDerivationStatusTypeId, string? reason, ReadingView? readingViewTypeId = null, string? locator = null)
        {
            Locator = locator;
            ReadingViewTypeId = readingViewTypeId;
            WorkId = workId;
            Title = title;
            ReadingFormatTypeId = readingFormatTypeId;
            HasPdf = hasPdf;
            ReadingDerivationStatusTypeId = readingDerivationStatusTypeId;
            Reason = reason;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.ReadingFormatTypeId))]
        public ReadingFormatType ReadingFormatTypeId { get; }

        // The book has a PDF file, whether it reads from it or from a reading copy.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.IsPdf))]
        public bool HasPdf { get; }

        // Where the PDF stands on its way to a reading copy; none when the module is off or the book has its own EPUB.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.ReadingDerivationStatusTypeIdOptional))]
        public ReadingDerivationStatus? ReadingDerivationStatusTypeId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Reason))]
        public string? Reason { get; }

        // The view the profile chose for this book; none is automatic.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.ReadingViewTypeId), false)]
        public ReadingView? ReadingViewTypeId { get; }

        // Where the profile stopped in this book, whichever view it was in.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.Locator))]
        public string? Locator { get; }
    }

    // The view of a book's PDF a profile chose for all its devices; none is automatic again.
    public sealed class SaveReadingViewDataV1 : BaseData
    {
        public SaveReadingViewDataV1(ReadingView? readingViewTypeId)
        {
            ReadingViewTypeId = readingViewTypeId;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.ReadingViewTypeId), false)]
        public ReadingView? ReadingViewTypeId { get; }
    }

    // Where a book's reading text stands, without opening it: which view is recommended once its text is prepared, and how far the recognition of a scan has come.
    public sealed class ReadingStateV1 : BaseResponse
    {
        public ReadingStateV1(bool hasPdf, ReadingView? recommendedView, int? recognitionPages, int recognizedPages, bool isRecognizing)
        {
            HasPdf = hasPdf;
            RecommendedView = recommendedView;
            RecognitionPages = recognitionPages;
            RecognizedPages = recognizedPages;
            IsRecognizing = isRecognizing;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.IsPdf))]
        public bool HasPdf { get; }

        // Smart once the prepared text passed its gate, Original where it did not; none while no text is prepared.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.ReadingViewTypeId), false)]
        public ReadingView? RecommendedView { get; }

        // Pages of a scan being read into text, once counted, and how many are read.
        [DataField(typeof(ReadingFields), nameof(ReadingFields.Pages))]
        public int? RecognitionPages { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Count))]
        public int RecognizedPages { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.IsPending))]
        public bool IsRecognizing { get; }
    }

    // A place in the reading text given by the page of the PDF, or the page a place in the reading text starts on.
    public sealed class ReadingPositionParametersV1 : BaseParameters
    {
        public ReadingPositionParametersV1(long mediaAssetId, int? page = null, int? sectionIndex = null, int? block = null)
        {
            MediaAssetId = mediaAssetId;
            Page = page;
            SectionIndex = sectionIndex;
            Block = block;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNumber), false)]
        public int? Page { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex), false)]
        public int? SectionIndex { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.BlockIndex), false)]
        public int? Block { get; }
    }

    public sealed class ReadingPositionV1 : BaseResponse
    {
        public ReadingPositionV1(int sectionIndex, int block, int page)
        {
            SectionIndex = sectionIndex;
            Block = block;
            Page = page;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.BlockIndex))]
        public int Block { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNumber))]
        public int Page { get; }
    }

    public sealed class ReadingPageNoteParametersV1 : BaseParameters
    {
        public ReadingPageNoteParametersV1(long mediaAssetId, long readingPageNoteId)
        {
            MediaAssetId = mediaAssetId;
            ReadingPageNoteId = readingPageNoteId;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNoteId))]
        public long ReadingPageNoteId { get; }
    }

    // A profile's note on a page of a book's PDF, with the first words of the page where it has text.
    public sealed class ReadingPageNoteV1 : BaseResponse
    {
        public ReadingPageNoteV1(long id, int pageNumber, string? anchor, string note)
        {
            Id = id;
            PageNumber = pageNumber;
            Anchor = anchor;
            Note = note;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNoteId))]
        public long Id { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNumber))]
        public int PageNumber { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageAnchor), false)]
        public string? Anchor { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNote))]
        public string Note { get; }
    }

    public sealed class CreateReadingPageNoteDataV1 : BaseData
    {
        public CreateReadingPageNoteDataV1(int pageNumber, string? anchor, string note)
        {
            PageNumber = pageNumber;
            Anchor = anchor;
            Note = note;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNumber))]
        public int PageNumber { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageAnchor), false)]
        public string? Anchor { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNote))]
        public string Note { get; }
    }

    public sealed class CreateReadingPageNoteResultV1 : BaseResponse
    {
        public CreateReadingPageNoteResultV1(long id)
        {
            Id = id;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.PageNoteId))]
        public long Id { get; }
    }

    public sealed class ReadingFileV1 : BaseResponse, IFileOutput
    {
        public ReadingFileV1(Stream content)
        {
            Content = content;
        }

        [JsonIgnore]
        public Stream Content { get; }

        [JsonIgnore]
        public string ContentType => "application/pdf";
    }
}
