using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class LanguageRulesParametersV1 : BaseParameters
    {
    }

    // The add-ons of a language, for every media type: release groups and words that are preferred or blocked in releases of that language.
    public sealed class LanguageRuleV1 : BaseResponse
    {
        public LanguageRuleV1(string language, string[] preferredGroups, string[] blockedGroups, string[] preferredWords, string[] blockedWords)
        {
            Language = language;
            PreferredGroups = preferredGroups;
            BlockedGroups = blockedGroups;
            PreferredWords = preferredWords;
            BlockedWords = blockedWords;
        }

        [DataField(typeof(LanguageRuleFields), nameof(LanguageRuleFields.Language))]
        public string Language { get; }

        [DataField(typeof(LanguageRuleFields), nameof(LanguageRuleFields.Entries))]
        public string[] PreferredGroups { get; }

        [DataField(typeof(LanguageRuleFields), nameof(LanguageRuleFields.Entries))]
        public string[] BlockedGroups { get; }

        [DataField(typeof(LanguageRuleFields), nameof(LanguageRuleFields.Entries))]
        public string[] PreferredWords { get; }

        [DataField(typeof(LanguageRuleFields), nameof(LanguageRuleFields.Entries))]
        public string[] BlockedWords { get; }
    }

    // The language is the address; every list empty removes the rule.
    public sealed class LanguageRuleParametersV1 : BaseParameters
    {
        public LanguageRuleParametersV1(string language)
        {
            Language = language;
        }

        [DataField(typeof(LanguageRuleFields), nameof(LanguageRuleFields.Language))]
        public string Language { get; }
    }

    public sealed class SaveLanguageRuleDataV1 : BaseData
    {
        public SaveLanguageRuleDataV1(string[] preferredGroups, string[] blockedGroups, string[] preferredWords, string[] blockedWords)
        {
            PreferredGroups = preferredGroups;
            BlockedGroups = blockedGroups;
            PreferredWords = preferredWords;
            BlockedWords = blockedWords;
        }

        [DataField(typeof(LanguageRuleFields), nameof(LanguageRuleFields.Entries))]
        public string[] PreferredGroups { get; }

        [DataField(typeof(LanguageRuleFields), nameof(LanguageRuleFields.Entries))]
        public string[] BlockedGroups { get; }

        [DataField(typeof(LanguageRuleFields), nameof(LanguageRuleFields.Entries))]
        public string[] PreferredWords { get; }

        [DataField(typeof(LanguageRuleFields), nameof(LanguageRuleFields.Entries))]
        public string[] BlockedWords { get; }
    }
}
