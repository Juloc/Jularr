using System.Collections.Frozen;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Localization;

namespace Jularr.Backend.Data.Public.Localization.V1
{
    public sealed class ReadUiTextsParametersV1 : BaseParameters
    {
        public ReadUiTextsParametersV1(long uiLocaleId)
        {
            UiLocaleId = uiLocaleId;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long UiLocaleId { get; }
    }

    public sealed class ReadUiTextsResultV1 : BaseResponse
    {
        public ReadUiTextsResultV1(long uiLocaleId, string locale, bool isRightToLeft,
            string version, IReadOnlyDictionary<string, string> texts)
        {
            UiLocaleId = uiLocaleId;
            Locale = locale;
            IsRightToLeft = isRightToLeft;
            Version = version;
            Texts = texts.ToFrozenDictionary(StringComparer.Ordinal);
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long UiLocaleId { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Locale))]
        public string Locale { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.IsRightToLeft))]
        public bool IsRightToLeft { get; }

        // Opaque revision covering every fallback layer in this complete language pack.
        public string Version { get; }
        public IReadOnlyDictionary<string, string> Texts { get; }
        public string this[string key] => Texts.GetValueOrDefault(key, key);
    }
}
