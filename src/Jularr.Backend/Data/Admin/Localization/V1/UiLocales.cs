using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Localization;

namespace Jularr.Backend.Data.Admin.Localization.V1
{
    public sealed class ReadUiLocalesParametersV1 : BaseParameters
    {
        public ReadUiLocalesParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class UpdateUiLocaleParametersV1 : BaseParameters
    {
        public UpdateUiLocaleParametersV1(long uiLocaleId)
        {
            UiLocaleId = uiLocaleId;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long UiLocaleId { get; }
    }

    public sealed class CreateUiLocaleParametersV1 : BaseParameters
    {
    }

    public sealed class UiLocaleDetailsV1 : BaseResponse
    {
        public UiLocaleDetailsV1(long id, string locale, string name, bool isEnabled, bool isRightToLeft, long catalogVersion)
        {
            Id = id;
            Locale = locale;
            Name = name;
            IsEnabled = isEnabled;
            IsRightToLeft = isRightToLeft;
            CatalogVersion = catalogVersion;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long Id { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Locale))]
        public string Locale { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Name))]
        public string Name { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.IsRightToLeft))]
        public bool IsRightToLeft { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.CatalogVersion))]
        public long CatalogVersion { get; }
    }

    public sealed class CreateUiLocaleDataV1 : BaseData
    {
        public CreateUiLocaleDataV1(string locale, string name, bool isRightToLeft)
        {
            Locale = locale;
            Name = name;
            IsRightToLeft = isRightToLeft;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Locale))]
        public string Locale { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Name))]
        public string Name { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.IsRightToLeft))]
        public bool IsRightToLeft { get; }
    }

    public sealed class UpdateUiLocaleDataV1 : BaseData
    {
        public UpdateUiLocaleDataV1(string name, bool isEnabled, bool isRightToLeft, long catalogVersion)
        {
            Name = name;
            IsEnabled = isEnabled;
            IsRightToLeft = isRightToLeft;
            CatalogVersion = catalogVersion;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Name))]
        public string Name { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.IsRightToLeft))]
        public bool IsRightToLeft { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.CatalogVersion))]
        public long CatalogVersion { get; }
    }

    public sealed class CreateUiLocaleResultV1 : BaseResponse
    {
        public CreateUiLocaleResultV1(long id)
        {
            Id = id;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long Id { get; }
    }
}
