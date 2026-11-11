using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Reading;
using Jularr.Backend.Data.Fields.Translation;

namespace Jularr.Backend.Data.Logic.Reading
{
    // One book into one language: how many of its sections are translated completely.
    internal sealed class BookTranslationProgressRowData : BaseData
    {
        public BookTranslationProgressRowData(long documentId, long workId, string title, string? volume, string language, int total, int done, int totalBlocks, int doneBlocks)
        {
            DocumentId = documentId;
            WorkId = workId;
            Title = title;
            Volume = volume;
            Language = language;
            Total = total;
            Done = done;
            TotalBlocks = totalBlocks;
            DoneBlocks = doneBlocks;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.WorkId))]
        public long WorkId { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Title))]
        public string Title { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Volume))]
        public string? Volume { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string Language { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Count))]
        public int Total { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Count))]
        public int Done { get; }

        // The blocks of all its sections and of those translated completely: what is left is the work still to do.
        [DataField(typeof(TranslationFields), nameof(TranslationFields.BlockCount))]
        public int TotalBlocks { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.BlockCount))]
        public int DoneBlocks { get; }
    }

    // Books whose progress is counted again, in every language a profile reads or only in the given one.
    internal sealed class BookTranslationRecountData : BaseData
    {
        public BookTranslationRecountData(long[] documentIds, string? language)
        {
            DocumentIds = documentIds;
            Language = language;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentIds))]
        public long[] DocumentIds { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language), false)]
        public string? Language { get; }
    }

    // The next books of the full count: from this document on, at most Count of them.
    internal sealed class BookTranslationBatchData : BaseData
    {
        public BookTranslationBatchData(long documentId, int count)
        {
            DocumentId = documentId;
            Count = count;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Count))]
        public int Count { get; }
    }

    // A language books are translated into.
    internal sealed class BookTranslationLanguageData : BaseData
    {
        public BookTranslationLanguageData(string language)
        {
            Language = language;
        }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string Language { get; }
    }
}
