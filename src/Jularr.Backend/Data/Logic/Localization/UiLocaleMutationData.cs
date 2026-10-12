using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Localization;

namespace Jularr.Backend.Data.Logic.Localization
{
    internal sealed class UiLocaleCreationData : BaseData
    {
        public UiLocaleCreationData(string locale, string name, bool isRightToLeft)
        {
            Locale = (string)UiLocaleFields.Locale.Normalize(locale)!;
            Name = (string)UiLocaleFields.Name.Normalize(name)!;
            IsRightToLeft = isRightToLeft;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Locale))]
        public string Locale { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Name))]
        public string Name { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.IsRightToLeft))]
        public bool IsRightToLeft { get; }
    }

    internal sealed class UiLocaleUpdateData : BaseData
    {
        public UiLocaleUpdateData(long id, string name, bool isEnabled, bool isRightToLeft, long catalogVersion)
        {
            Id = id;
            Name = (string)UiLocaleFields.Name.Normalize(name)!;
            IsEnabled = isEnabled;
            IsRightToLeft = isRightToLeft;
            CatalogVersion = catalogVersion;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long Id { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Name))]
        public string Name { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.IsRightToLeft))]
        public bool IsRightToLeft { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.CatalogVersion))]
        public long CatalogVersion { get; }
    }
}
