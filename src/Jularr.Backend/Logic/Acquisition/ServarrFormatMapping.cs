using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.Servarr;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class ServarrFormatMapping
    {
        public static CustomFormatImportData Map(ServarrCustomFormat format, ServarrFormatCatalog catalog, MediaType mediaType)
        {
            var conditions = format.Conditions.Select(condition => MapCondition(condition, catalog)).ToArray();
            CustomFormatLogic.ValidateConditions(mediaType, conditions);
            return new CustomFormatImportData(format.Id, format.Name, format.IncludeWhenRenaming, conditions);
        }

        private static FormatCondition MapCondition(ServarrFormatCondition condition, ServarrFormatCatalog catalog)
        {
            var kind = condition.Implementation switch
            {
                "ReleaseTitleSpecification" => FormatConditionKind.ReleaseTitle,
                "EditionSpecification" => FormatConditionKind.Edition,
                "LanguageSpecification" => FormatConditionKind.Language,
                "ReleaseGroupSpecification" => FormatConditionKind.ReleaseGroup,
                "SourceSpecification" => FormatConditionKind.Source,
                "ResolutionSpecification" => FormatConditionKind.Resolution,
                "QualityModifierSpecification" => FormatConditionKind.QualityModifier,
                "IndexerFlagSpecification" => FormatConditionKind.IndexerFlag,
                "SizeSpecification" => FormatConditionKind.Size,
                "ReleaseTypeSpecification" => FormatConditionKind.ReleaseType,
                "YearSpecification" => FormatConditionKind.Year,
                _ => throw new DataValidationException("Conditions", "unknown_catalog_value")
            };
            var range = kind is FormatConditionKind.Size or FormatConditionKind.Year;
            var names = range ? new[] { "min", "max" } : kind == FormatConditionKind.Language ? ["value", "exceptLanguage"] : ["value"];
            if (condition.Fields.Keys.Any(name => !names.Contains(name, StringComparer.OrdinalIgnoreCase))) throw new DataValidationException("Conditions", "unknown_catalog_value");
            var value = condition.Fields.GetValueOrDefault("value");
            if (condition.Fields.TryGetValue("exceptLanguage", out var except) && except.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new DataValidationException("Conditions", "invalid_type");
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
            string? mapped = kind switch
            {
                FormatConditionKind.Language => Language(Option(value, catalog.Languages)),
                FormatConditionKind.IndexerFlag => Option(value, catalog.IndexerFlags),
                FormatConditionKind.Source or FormatConditionKind.QualityModifier => Token(Option(value, Options(condition, catalog))),
                FormatConditionKind.ReleaseType => Token(Option(value, Options(condition, catalog))) switch
                {
                    "singleepisode" => "singleEpisode",
                    "multiepisode" => "multiEpisode",
                    "seasonpack" => "seasonPack",
                    "unknown" => "unknown",
                    _ => throw new DataValidationException("Conditions", "unknown_catalog_value")
                },
                _ => text
            };
            return new FormatCondition(condition.Name, kind, condition.Negate, condition.Required)
            {
                Value = kind is FormatConditionKind.Size or FormatConditionKind.Year or FormatConditionKind.Resolution ? null : mapped,
                ExceptLanguage = condition.Fields.GetValueOrDefault("exceptLanguage").ValueKind == JsonValueKind.True,
                Resolution = kind == FormatConditionKind.Resolution ? Resolution(Option(value, Options(condition, catalog))) : null,
                Minimum = range ? Number(condition.Fields.GetValueOrDefault("min")) : null,
                Maximum = range && Number(condition.Fields.GetValueOrDefault("max")) is > 0 and var maximum ? maximum : null
            };
        }

        private static IReadOnlyDictionary<int, string> Options(ServarrFormatCondition condition, ServarrFormatCatalog catalog) =>
            catalog.Options.GetValueOrDefault(condition.Implementation + "/value") ?? new Dictionary<int, string>();

        private static string Option(JsonElement value, IReadOnlyDictionary<int, string> options)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var id))
            {
                return options.GetValueOrDefault(id) ?? throw new DataValidationException("Conditions", "unknown_catalog_value");
            }

            return value.ValueKind == JsonValueKind.String && value.GetString() is { } text ? text : throw new DataValidationException("Conditions", "invalid_type");
        }

        private static string Token(string value) => new(value.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        private static int Resolution(string value) => int.TryParse(new string(value.Where(char.IsAsciiDigit).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out var resolution)
            ? resolution : throw new DataValidationException("Conditions", "invalid_type");

        private static decimal? Number(JsonElement value)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number)) throw new DataValidationException("Conditions", "invalid_type");
            if (number < 0) throw new DataValidationException("Conditions", "out_of_range");
            return number;
        }

        private static string Language(string value)
        {
            if (value.Equals("original", StringComparison.OrdinalIgnoreCase)) return "original";
            if (value.Equals("any", StringComparison.OrdinalIgnoreCase)) return "any";
            if (value.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return "unknown";
            if (value.Equals("Flemish", StringComparison.OrdinalIgnoreCase)) return "nl";
            var name = value.Split('(')[0].Trim();
            return LanguageCodes.ToTwoLetter(value) ?? CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .FirstOrDefault(culture => culture.EnglishName.Equals(name, StringComparison.OrdinalIgnoreCase))?.TwoLetterISOLanguageName
                ?? throw new DataValidationException("Conditions", "unknown_catalog_value");
        }
    }
}
