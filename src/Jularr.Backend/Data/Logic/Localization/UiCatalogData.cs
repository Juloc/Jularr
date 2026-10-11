using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Localization;

namespace Jularr.Backend.Data.Logic.Localization
{
    public sealed class UiLocaleData : BaseData
    {
        public UiLocaleData(long id, string locale, bool isEnabled, bool isRightToLeft, long catalogVersion)
        {
            Id = id;
            Locale = locale;
            IsEnabled = isEnabled;
            IsRightToLeft = isRightToLeft;
            CatalogVersion = catalogVersion;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long Id { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Locale))]
        public string Locale { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.IsRightToLeft))]
        public bool IsRightToLeft { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.CatalogVersion))]
        public long CatalogVersion { get; }
    }

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

    public sealed class UiLocaleParameters : BaseData
    {
        public UiLocaleParameters(long uiLocaleId)
        {
            UiLocaleId = uiLocaleId;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long UiLocaleId { get; }
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
}
