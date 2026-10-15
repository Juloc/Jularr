using System.Globalization;
using Jularr.Backend.Data.Enums.Translation;
using Jularr.Integrations.Data.Translation;

namespace Jularr.Backend.Logic.Translation
{
    // Which Mozilla files translate between two languages: a direct model to or from English, otherwise both through English.
    internal static class TranslationModels
    {
        // The engine major version the published first-collection models run on.
        public const int EngineMajorVersion = 3;
        private static readonly string[] Vocabularies = ["vocab", "srcvocab", "trgvocab"];

        public static string Code(TranslationLanguageType language) => language switch
        {
            TranslationLanguageType.English => "en",
            TranslationLanguageType.German => "de",
            TranslationLanguageType.Indonesian => "id",
            TranslationLanguageType.Japanese => "ja",
            _ => throw new ArgumentOutOfRangeException(nameof(language))
        };

        public static IReadOnlyList<(TranslationLanguageType From, TranslationLanguageType To)> Route(TranslationLanguageType source, TranslationLanguageType target) =>
            source == target ? []
            : source == TranslationLanguageType.English || target == TranslationLanguageType.English ? [(source, target)]
            : [(source, TranslationLanguageType.English), (TranslationLanguageType.English, target)];

        // The newest released version with a model and its vocabulary for every build; files for single builds and parts the engine does not use stay out.
        public static IReadOnlyList<TranslationFile>? Model(IEnumerable<TranslationFile> files, TranslationLanguageType from, TranslationLanguageType to)
        {
            var (source, target) = (Code(from), Code(to));
            return files.Where(file => file.FromLanguage == source && file.ToLanguage == target && !file.IsFiltered && Version(file.Version) is not null
                    && (file.FileType is "model" or "lex" || Vocabularies.Contains(file.FileType)))
                .GroupBy(file => file.Version)
                .Where(version => version.Count(file => file.FileType == "model") == 1
                    && (version.Any(file => file.FileType == "vocab") || version.Any(file => file.FileType == "srcvocab") && version.Any(file => file.FileType == "trgvocab")))
                .OrderByDescending(version => Version(version.Key))
                .Select(version => (IReadOnlyList<TranslationFile>)[.. version])
                .FirstOrDefault();
        }

        public static TranslationFile? Engine(IEnumerable<TranslationFile> files) =>
            files.Where(file => file.Name == "bergamot-translator" && !file.IsFiltered && Version(file.Version) is { Major: EngineMajorVersion })
                .OrderByDescending(file => Version(file.Version)).FirstOrDefault();

        // "2.1" is released; "1.0a1" and other suffixes are previews.
        private static System.Version? Version(string text) =>
            System.Version.TryParse(text, out var version) && text.All(character => char.IsAsciiDigit(character) || character == '.') ? version : null;

        // Firefox picks the multiplication by the model's file name.
        public static string Precision(IReadOnlyList<TranslationFile> model) =>
            model.Single(file => file.FileType == "model").Name.EndsWith("intgemm8.bin", StringComparison.Ordinal) ? "int8shiftAll" : "int8shiftAlphaAll";

        // Served under its hash; the engine is listed without an extension.
        public static string FileName(TranslationFile file) => file.Hash + (Path.GetExtension(file.Name) is { Length: > 1 } extension ? extension.ToLower(CultureInfo.InvariantCulture)
            : file.FileType is null ? ".wasm" : ".bin");
    }
}
