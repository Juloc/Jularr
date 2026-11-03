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

    // The model the job worked with last and its version, for the progress: a section counts as finished only from that model in that version.
    // Both are none before the job's first round since the start.
    internal sealed class BookTranslationCurrentModelData : BaseData
    {
        public BookTranslationCurrentModelData(string? model, string? digest)
        {
            Model = model;
            Digest = digest;
        }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.OptionalName))]
        public string? Model { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.OptionalName))]
        public string? Digest { get; }
    }

    // One section of a book in one language, whose block choices and cached translations are read together.
    internal sealed class ReadingTranslationSectionData : BaseData
    {
        public ReadingTranslationSectionData(long documentId, int sectionIndex, string language, string[] paths)
        {
            DocumentId = documentId;
            SectionIndex = sectionIndex;
            Language = language;
            Paths = paths;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string Language { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Paths))]
        public string[] Paths { get; }
    }

    // A block whose translation comes from another model than the default, for every reader of the book.
    internal sealed class ReadingTranslationChoiceData : BaseData
    {
        public ReadingTranslationChoiceData(int blockIndex, string model)
        {
            BlockIndex = blockIndex;
            Model = model;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.BlockIndex))]
        public int BlockIndex { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Name))]
        public string Model { get; }
    }

    // A re-translation with a model for every reader of the book: one block (both indices), every block of a section (no block) or of the whole
    // book (neither).
    internal sealed class ReadingTranslationChoiceWriteData : BaseData
    {
        public ReadingTranslationChoiceWriteData(long documentId, int? sectionIndex, int? blockIndex, string language, string model)
        {
            DocumentId = documentId;
            SectionIndex = sectionIndex;
            BlockIndex = blockIndex;
            Language = language;
            Model = model;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex), false)]
        public int? SectionIndex { get; }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.BlockIndex), false)]
        public int? BlockIndex { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string Language { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Name))]
        public string Model { get; }
    }

    // A model id someone re-translated passages with.
    internal sealed class ReadingTranslationChosenModelData : BaseData
    {
        public ReadingTranslationChosenModelData(string chosenModel)
        {
            ChosenModel = chosenModel;
        }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Name))]
        public string ChosenModel { get; }
    }

    // One model's cached translation of a section.
    internal sealed class ReadingTranslationCacheData : BaseData
    {
        public ReadingTranslationCacheData(string path, string cache)
        {
            Path = path;
            Cache = cache;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.Path))]
        public string Path { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Cache))]
        public string Cache { get; }
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
