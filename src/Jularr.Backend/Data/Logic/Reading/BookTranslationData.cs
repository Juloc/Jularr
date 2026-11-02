using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Reading;
using Jularr.Backend.Data.Fields.Translation;

namespace Jularr.Backend.Data.Logic.Reading
{
    // The model a round translates with; a section counts as done for it only when its cache is complete and from this model.
    internal sealed class BookTranslationModelData : BaseData
    {
        public BookTranslationModelData(string model, string digest)
        {
            Model = model;
            Digest = digest;
        }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Name))]
        public string Model { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Name))]
        public string Digest { get; }
    }

    // A cached section of a book still to translate into one language: its text and the blocks translated so far, if any.
    internal sealed class BookTranslationUnitData : BaseData
    {
        public BookTranslationUnitData(long documentId, int sectionIndex, string language, string sourceLanguage, string html, int blocks, string? cache)
        {
            DocumentId = documentId;
            SectionIndex = sectionIndex;
            Language = language;
            SourceLanguage = sourceLanguage;
            Html = html;
            Blocks = blocks;
            Cache = cache;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string Language { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string SourceLanguage { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Html))]
        public string Html { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int Blocks { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Cache))]
        public string? Cache { get; }
    }
}
