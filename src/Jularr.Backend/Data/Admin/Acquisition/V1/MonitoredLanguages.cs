using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    public sealed class MonitoredLanguagesParametersV1 : BaseParameters
    {
    }

    // The languages a title nobody requested is wanted in: every profile's first language, or one fixed chain.
    public sealed class MonitoredLanguagesV1 : BaseResponse
    {
        public MonitoredLanguagesV1(MonitoredLanguageMode modeId, string? firstLanguage, string? secondLanguage, bool usesOriginal, string[] languages)
        {
            ModeId = modeId;
            FirstLanguage = firstLanguage;
            SecondLanguage = secondLanguage;
            UsesOriginal = usesOriginal;
            Languages = languages;
        }

        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.ModeId))]
        public MonitoredLanguageMode ModeId { get; }

        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.FirstLanguage), false)]
        public string? FirstLanguage { get; }

        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.SecondLanguage), false)]
        public string? SecondLanguage { get; }

        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.UsesOriginal))]
        public bool UsesOriginal { get; }

        // The first language of every profile, as "every profile" wants them today.
        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.Languages))]
        public string[] Languages { get; }
    }

    public sealed class SaveMonitoredLanguagesDataV1 : BaseData
    {
        public SaveMonitoredLanguagesDataV1(MonitoredLanguageMode modeId, string? firstLanguage, string? secondLanguage, bool usesOriginal)
        {
            ModeId = modeId;
            FirstLanguage = firstLanguage;
            SecondLanguage = secondLanguage;
            UsesOriginal = usesOriginal;
        }

        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.ModeId))]
        public MonitoredLanguageMode ModeId { get; }

        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.FirstLanguage), false)]
        public string? FirstLanguage { get; }

        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.SecondLanguage), false)]
        public string? SecondLanguage { get; }

        [DataField(typeof(LanguageChainFields), nameof(LanguageChainFields.UsesOriginal))]
        public bool UsesOriginal { get; }
    }
}
