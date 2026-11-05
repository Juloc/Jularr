using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Admin.Wanted.V1
{
    // The languages a target is wanted in: its own chain, or the chains of its requests, or the setting for titles nobody requested.
    public sealed class WantedLanguagesV1 : BaseResponse
    {
        public WantedLanguagesV1(bool hasOwnChain, string? firstLanguage, string? secondLanguage, bool usesOriginal, string[] languages, string[] missingLanguages)
        {
            HasOwnChain = hasOwnChain;
            FirstLanguage = firstLanguage;
            SecondLanguage = secondLanguage;
            UsesOriginal = usesOriginal;
            Languages = languages;
            MissingLanguages = missingLanguages;
        }

        // An administrator's own chain for this title; without one the title follows its requests or the setting for monitored titles.
        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.HasOwnChain))]
        public bool HasOwnChain { get; }

        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.FirstLanguage), false)]
        public string? FirstLanguage { get; }

        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.SecondLanguage), false)]
        public string? SecondLanguage { get; }

        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.UsesOriginal))]
        public bool UsesOriginal { get; }

        // The first languages the title is wanted in now, and those no present file covers yet (books).
        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.Languages))]
        public string[] Languages { get; }

        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.Languages))]
        public string[] MissingLanguages { get; }
    }

    // No first language takes the title's own chain away.
    public sealed class SaveWantedLanguagesDataV1 : BaseData
    {
        public SaveWantedLanguagesDataV1(string? firstLanguage, string? secondLanguage, bool usesOriginal)
        {
            FirstLanguage = firstLanguage;
            SecondLanguage = secondLanguage;
            UsesOriginal = usesOriginal;
        }

        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.FirstLanguage), false)]
        public string? FirstLanguage { get; }

        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.SecondLanguage), false)]
        public string? SecondLanguage { get; }

        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.UsesOriginal))]
        public bool UsesOriginal { get; }
    }
}
