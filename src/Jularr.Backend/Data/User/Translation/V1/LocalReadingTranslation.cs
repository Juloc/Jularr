using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Fields.Reading;
using Jularr.Backend.Data.Fields.Translation;

namespace Jularr.Backend.Data.User.Translation.V1
{
    public sealed class LocalTranslationAvailabilityParametersV1 : BaseParameters
    {
    }

    public sealed class LocalTranslationAvailabilityV1(string? model) : BaseResponse
    {
        public string? Model { get; } = model;
    }

    public sealed class LocalReadingTranslationParametersV1(long documentId, int sectionIndex, string language, string model, string sourceLanguage) : BaseParameters
    {
        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; } = documentId;

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; } = sectionIndex;

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string Language { get; } = language;

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Name))]
        public string Model { get; } = model;

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string SourceLanguage { get; } = sourceLanguage;
    }

    // A section's server translation: Blocks is a JSON array with one text per block (empty when nothing is translated yet), Models the model id
    // each text comes from (null without text) and Stale whether it is from an earlier version of that model, shown until the new one replaces it.
    public sealed class LocalReadingTranslationV1(string blocks, string?[] models, bool[] stale) : BaseResponse
    {
        [DataField(typeof(TranslationFields), nameof(TranslationFields.Blocks))]
        public string Blocks { get; } = blocks;

        public string?[] Models { get; } = models;
        public bool[] Stale { get; } = stale;
    }

    public sealed class LocalReadingBlockParametersV1(long documentId, int sectionIndex, string language, string model, int blockIndex) : BaseParameters
    {
        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; } = documentId;

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; } = sectionIndex;

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string Language { get; } = language;

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Name))]
        public string Model { get; } = model;

        [DataField(typeof(ReadingFields), nameof(ReadingFields.BlockIndex))]
        public int BlockIndex { get; } = blockIndex;
    }

    public sealed class LocalReadingBlockDataV1(string sourceLanguage) : BaseData
    {
        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string SourceLanguage { get; } = sourceLanguage;
    }

    public sealed class LocalReadingBlockV1(string text) : BaseResponse
    {
        public string Text { get; } = text;
    }

    public sealed class ReaderTranslationModelsParametersV1 : BaseParameters
    {
    }

    // The provider-qualified model ids the caller may re-translate with now; an admin-only provider's models only for an administrator.
    public sealed class ReaderTranslationModelsV1(string[] models) : BaseResponse
    {
        [DataField(typeof(TranslationFields), nameof(TranslationFields.Models))]
        public string[] Models { get; } = models;
    }

    public sealed class LocalReadingSectionModelParametersV1(long documentId, int sectionIndex, string language, string model) : BaseParameters
    {
        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; } = documentId;

        [DataField(typeof(ReadingFields), nameof(ReadingFields.SectionIndex))]
        public int SectionIndex { get; } = sectionIndex;

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string Language { get; } = language;

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Name))]
        public string Model { get; } = model;
    }

    // A re-translation with the model: one block of the section (BlockIndex), the whole section, or every section of the book.
    public sealed class LocalReadingChoiceDataV1(ReadingTranslationScope scope, int? blockIndex) : BaseData
    {
        [DataField(typeof(ReadingFields), nameof(ReadingFields.TranslationScopeId))]
        public ReadingTranslationScope Scope { get; } = scope;

        [DataField(typeof(ReadingFields), nameof(ReadingFields.BlockIndex), false)]
        public int? BlockIndex { get; } = blockIndex;
    }

    // Translated is the number of blocks that now show the new translation; Kept those whose answer did not pass the checks and stay as they were.
    public sealed class LocalReadingRetranslationV1(int translated, int kept) : BaseResponse
    {
        public int Translated { get; } = translated;
        public int Kept { get; } = kept;
    }
}
