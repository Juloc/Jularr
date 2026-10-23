using Jularr.Backend.Data.Enums.Localization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Localization;

namespace Jularr.Backend.Data.Logic.Localization
{
    public sealed class UiCatalogTextData : BaseData
    {
        public UiCatalogTextData(string key, string text)
        {
            Key = key;
            Text = text;
        }

        [DataField(typeof(UiTranslationMessageFields), nameof(UiTranslationMessageFields.Key))]
        public string Key { get; }

        [DataField(typeof(UiTranslationFields), nameof(UiTranslationFields.Text), true)]
        public string Text { get; }
    }

    internal sealed class UiResourceBatchData : BaseData
    {
        public UiResourceBatchData(IReadOnlyList<UiTextResource> resources)
        {
            Keys = resources.Select(resource => resource.Key).ToArray();
            EnglishTexts = resources.Select(resource => resource.EnglishText).ToArray();
        }

        public string[] Keys { get; }
        public string[] EnglishTexts { get; }
    }

    // A shipped language and its texts for the current English.
    internal sealed class UiShippedCatalogData : BaseData
    {
        public UiShippedCatalogData(string locale, string name, bool isRightToLeft, bool isEnabled, UiTranslationStatusType uiTranslationStatusTypeId, string[] keys, string[] texts)
        {
            Locale = locale;
            Name = name;
            IsRightToLeft = isRightToLeft;
            IsEnabled = isEnabled;
            UiTranslationStatusTypeId = uiTranslationStatusTypeId;
            Keys = keys;
            Texts = texts;
        }

        public string Locale { get; }
        public string Name { get; }
        public bool IsRightToLeft { get; }
        public bool IsEnabled { get; }
        public UiTranslationStatusType UiTranslationStatusTypeId { get; }
        public string[] Keys { get; }
        public string[] Texts { get; }
    }
}
