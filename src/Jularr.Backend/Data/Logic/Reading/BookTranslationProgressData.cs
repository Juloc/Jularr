using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Reading;
using Jularr.Backend.Data.Fields.Translation;

namespace Jularr.Backend.Data.Logic.Reading
{
    // One book into one language: how many of its sections are translated completely.
    internal sealed class BookTranslationProgressRowData : BaseData
    {
        public BookTranslationProgressRowData(long documentId, string title, string? volume, string language, int total, int done, int totalBlocks, int doneBlocks)
        {
            DocumentId = documentId;
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
}
