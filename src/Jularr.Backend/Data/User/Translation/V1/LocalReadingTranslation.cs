using Jularr.Backend.Data.Common;
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
}
