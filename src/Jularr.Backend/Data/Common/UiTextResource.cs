using Jularr.Backend.Data.Fields.Localization;

namespace Jularr.Backend.Data.Common
{
    // Source declarations contain English only. Translation context belongs to PostgreSQL.
    public sealed class UiTextResource : BaseData
    {
        public UiTextResource(string key, string englishText)
        {
            Key = key;
            EnglishText = englishText;
        }

        [DataField(typeof(UiTranslationMessageFields), nameof(UiTranslationMessageFields.Key))]
        public string Key { get; }

        [DataField(typeof(UiTranslationMessageFields), nameof(UiTranslationMessageFields.EnglishText))]
        public string EnglishText { get; }
    }
}
