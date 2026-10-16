using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Translation;

namespace Jularr.Backend.Data.User.Translation.V1
{
    public sealed class TranslationRouteParametersV1 : BaseParameters
    {
        public TranslationRouteParametersV1(string sourceLanguage, string targetLanguage)
        {
            SourceLanguage = sourceLanguage;
            TargetLanguage = targetLanguage;
        }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string SourceLanguage { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string TargetLanguage { get; }
    }

    public sealed class TranslationLanguagesParametersV1 : BaseParameters
    {
    }

    // The languages a book can be translated from and into; English bridges any two.
    public sealed class TranslationLanguagesV1 : BaseResponse
    {
        public TranslationLanguagesV1(string[] sources, string[] targets)
        {
            Sources = sources;
            Targets = targets;
        }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Languages))]
        public string[] Sources { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Languages))]
        public string[] Targets { get; }
    }

    // One file the browser loads: the engine at step 0, then each model step's parts with the multiplication its model needs.
    public sealed class TranslationFileV1 : BaseResponse
    {
        public TranslationFileV1(int step, string fileType, string url, string? precision)
        {
            Step = step;
            FileType = fileType;
            Url = url;
            Precision = precision;
        }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Step))]
        public int Step { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.FileType))]
        public string FileType { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Url))]
        public string Url { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Precision))]
        public string? Precision { get; }
    }

    public sealed class TranslationFileParametersV1 : BaseParameters
    {
        public TranslationFileParametersV1(string name)
        {
            Name = name;
        }

        // The file's hash and extension.
        [DataField(typeof(TranslationFields), nameof(TranslationFields.Name))]
        public string Name { get; }
    }

    // A file named by its content: it never changes, browsers keep it.
    public sealed class TranslationFileContentV1 : BaseResponse, IImmutableFileOutput
    {
        public TranslationFileContentV1(string filePath, string contentType)
        {
            FilePath = filePath;
            ContentType = contentType;
        }

        [JsonIgnore]
        public string FilePath { get; }

        [JsonIgnore]
        public string ContentType { get; }
    }
}
