using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Reading;
using Jularr.Backend.Data.Fields.Reading;
using Jularr.Backend.Data.Fields.Translation;

namespace Jularr.Backend.Data.User.Translation.V1
{
    public sealed class LocalTranslationAvailabilityParametersV1 : BaseParameters
    {
    }

    // The model id that translates for every reader now (a local machine's, never Codex's); Away when none translates because the machines set up for
    // translation do not answer. Codex is the model id a holder of the admin-only right translates a chapter with when pressing the button, CodexProblem
    // what keeps it from doing so now ("not_connected", "limit" or "unavailable"); both are empty for everyone else.
    public sealed class LocalTranslationAvailabilityV1(string? model, bool away = false, string? codex = null, string? codexProblem = null) : BaseResponse
    {
        public string? Model { get; } = model;
        public bool Away { get; } = away;
        public string? Codex { get; } = codex;
        public string? CodexProblem { get; } = codexProblem;
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

    // The next stretch of a chapter for a model that translates in several requests: translate from this block onward and round again to the start, leaving
    // out the blocks whose answers failed the checks earlier in this run.
    public sealed class LocalReadingChunkDataV1(string sourceLanguage, int from, int[] skip) : BaseData
    {
        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string SourceLanguage { get; } = sourceLanguage;

        [DataField(typeof(ReadingFields), nameof(ReadingFields.BlockIndex))]
        public int From { get; } = from;

        [DataField(typeof(ReadingFields), nameof(ReadingFields.BlockIndexes))]
        public int[] Skip { get; } = skip;
    }

    // One request's blocks, each with its text; Failed the blocks whose answer failed the checks even asked alone; Characters the source characters the request
    // carried and Seconds the time the model needed, with the blocks and source characters still missing in this version of the model (without Failed and the ones
    // skipped), from which the Reader estimates the time left.
    public sealed class LocalReadingChunkV1(LocalReadingChunkBlockV1[] blocks, int[] failed, int characters, double seconds, int remaining, int remainingCharacters) : BaseResponse
    {
        public LocalReadingChunkBlockV1[] Blocks { get; } = blocks;
        public int[] Failed { get; } = failed;
        public int Characters { get; } = characters;
        public double Seconds { get; } = seconds;
        public int Remaining { get; } = remaining;
        public int RemainingCharacters { get; } = remainingCharacters;
    }

    public sealed class LocalReadingChunkBlockV1(int index, string text)
    {
        public int Index { get; } = index;
        public string Text { get; } = text;
    }

    public sealed class LocalReadingBlockV1(string text) : BaseResponse
    {
        public string Text { get; } = text;
    }

    public sealed class ReaderTranslationModelsParametersV1 : BaseParameters
    {
    }

    // The models the caller may re-translate with now; an admin-only provider's models only with the admin-only right.
    public sealed class ReaderTranslationModelsV1(ReaderTranslationModelV1[] models) : BaseResponse
    {
        public ReaderTranslationModelV1[] Models { get; } = models;
    }

    // A provider-qualified model id; Passages when the Reader re-translates a chapter with it in whole-passage requests (Codex) rather than block by block.
    public sealed class ReaderTranslationModelV1(string id, bool passages)
    {
        public string Id { get; } = id;
        public bool Passages { get; } = passages;
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
