using System.Globalization;
using Jularr.Integrations.Data.Translation;

namespace Jularr.Backend.Logic.Translation
{
    // Which Mozilla files translate between two languages: a direct model to or from English, otherwise both through English.
    internal static class TranslationModels
    {
        // The engine major version the published first-collection models run on.
        public const int EngineMajorVersion = 3;
        private static readonly string[] Vocabularies = ["vocab", "srcvocab", "trgvocab"];

        private const string Bridge = "en";

        // Into English and out of it; English itself needs one step.
        public static IReadOnlyList<(string From, string To)> Route(string source, string target) =>
            source == target ? []
            : source == Bridge || target == Bridge ? [(source, target)]
            : [(source, Bridge), (Bridge, target)];

        // Every language with released models into English (sources) and out of it (targets), English among both.
        public static (string[] Sources, string[] Targets) Languages(IReadOnlyList<TranslationFile> files)
        {
            // API routes and response fields currently accept only ISO 639 language codes.
            // Mozilla also publishes region/script variants (for example pt-BR and zh-Hans);
            // advertising those would fail response validation and break the whole Reader.
            var languages = files.SelectMany(file => new[] { file.FromLanguage, file.ToLanguage }).OfType<string>()
                .Where(code => code != Bridge && code.Length is >= 2 and <= 3 && code.All(char.IsAsciiLetterLower))
                .Distinct(StringComparer.Ordinal).ToArray();
            var fromEnglish = languages.Where(code => Model(files, Bridge, code) is not null).Order(StringComparer.Ordinal).ToArray();
            var intoEnglish = languages.Where(code => Model(files, code, Bridge) is not null).Order(StringComparer.Ordinal).ToArray();
            // English is a bridge only if a real model exists in the required direction.
            string[] sources = fromEnglish.Length > 0 ? [Bridge, .. intoEnglish] : intoEnglish;
            string[] targets = intoEnglish.Length > 0 ? [Bridge, .. fromEnglish] : fromEnglish;
            return (sources, targets);
        }

        // The newest released version with a model and its vocabulary for every build; files for single builds and parts the engine does not use stay out.
        public static IReadOnlyList<TranslationFile>? Model(IEnumerable<TranslationFile> files, string source, string target)
        {
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
