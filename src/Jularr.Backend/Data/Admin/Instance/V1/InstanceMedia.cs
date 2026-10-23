using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Instance.V1
{
    public sealed class ReadInstanceMediaParametersV1 : BaseParameters
    {
    }

    public sealed class UpdateInstanceMediaParametersV1 : BaseParameters
    {
    }

    public sealed class InstanceMediaDetailsV1 : BaseResponse
    {
        public InstanceMediaDetailsV1(string key, bool isSubtype, bool isEnabled, bool hasLibrary, long version)
        {
            Key = key;
            IsSubtype = isSubtype;
            IsEnabled = isEnabled;
            HasLibrary = hasLibrary;
            Version = version;
        }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.Key))]
        public string Key { get; }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.IsSubtype))]
        public bool IsSubtype { get; }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.IsEnabled))]
        public bool IsEnabled { get; }

        // An enabled category without a library receives no downloads.
        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.HasLibrary))]
        public bool HasLibrary { get; }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.Version))]
        public long Version { get; }
    }

    public sealed class UpdateInstanceMediaDataV1 : BaseData
    {
        public UpdateInstanceMediaDataV1(string key, bool isEnabled, long version)
        {
            Key = key;
            IsEnabled = isEnabled;
            Version = version;
        }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.Key))]
        public string Key { get; }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.Version))]
        public long Version { get; }

        public override void Validate()
        {
            base.Validate();
            if (!MediaCategories.IsKey(Key))
            {
                throw new DataValidationException(nameof(Key), "unknown_catalog_value");
            }
        }
    }
}
