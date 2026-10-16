using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Localization;

namespace Jularr.Backend.Data.Public.Setup.V1
{
    public sealed class SetupLocaleV1 : BaseResponse
    {
        public SetupLocaleV1(long id, string locale, string name, bool isMachineTranslated)
        {
            Id = id;
            Locale = locale;
            Name = name;
            IsMachineTranslated = isMachineTranslated;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long Id { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Locale))]
        public string Locale { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Name))]
        public string Name { get; }

        [DataField(typeof(UiLocaleTranslationFields), nameof(UiLocaleTranslationFields.IsMachineTranslated))]
        public bool IsMachineTranslated { get; }
    }
}
