using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Localization;

namespace Jularr.Backend.Data.Public.Localization.V1
{
    public sealed class ReadUiLocalesParametersV1 : BaseParameters
    {
    }

    public sealed class UiLocaleDetailsV1 : BaseResponse
    {
        public UiLocaleDetailsV1(long id, string locale, string name, bool isRightToLeft)
        {
            Id = id;
            Locale = locale;
            Name = name;
            IsRightToLeft = isRightToLeft;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long Id { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Locale))]
        public string Locale { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Name))]
        public string Name { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.IsRightToLeft))]
        public bool IsRightToLeft { get; }
    }
}
