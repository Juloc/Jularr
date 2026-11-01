using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Reading;
using Jularr.Backend.Data.Fields.Translation;

namespace Jularr.Backend.Data.Logic.Reading
{
    // One book into one language: how many of its sections are translated completely.
    internal sealed class BookTranslationProgressRowData : BaseData
    {
        public BookTranslationProgressRowData(long documentId, string title, string language, int total, int done)
        {
            DocumentId = documentId;
            Title = title;
            Language = language;
            Total = total;
            Done = done;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Title))]
        public string Title { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string Language { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Count))]
        public int Total { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Count))]
        public int Done { get; }
    }
}
