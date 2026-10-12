using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Admin.Instance.V1
{
    public sealed class ReadInstanceSettingsParametersV1 : BaseParameters
    {
    }

    public sealed class UpdateInstanceSettingsParametersV1 : BaseParameters
    {
    }

    public sealed class InstanceSettingsDetailsV1 : BaseResponse
    {
        public InstanceSettingsDetailsV1(string name, long uiLocaleId, long version)
        {
            Name = name;
            UiLocaleId = uiLocaleId;
            Version = version;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Name))]
        public string Name { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UiLocaleId))]
        public long UiLocaleId { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Version))]
        public long Version { get; }
    }

    public sealed class UpdateInstanceSettingsDataV1 : BaseData
    {
        public UpdateInstanceSettingsDataV1(string name, long uiLocaleId, long version)
        {
            Name = name;
            UiLocaleId = uiLocaleId;
            Version = version;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Name))]
        public string Name { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.UiLocaleId))]
        public long UiLocaleId { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Version))]
        public long Version { get; }
    }
}
