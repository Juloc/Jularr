using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Admin.Instance.V1
{
    public sealed class ReadInstanceModulesParametersV1 : BaseParameters
    {
    }

    public sealed class UpdateInstanceModuleParametersV1 : BaseParameters
    {
        public UpdateInstanceModuleParametersV1(long instanceModuleId)
        {
            InstanceModuleId = instanceModuleId;
        }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Id))]
        public long InstanceModuleId { get; }
    }

    public sealed class InstanceModuleDetailsV1 : BaseResponse
    {
        public InstanceModuleDetailsV1(long id, string key, bool isEnabled, long version)
        {
            Id = id;
            Key = key;
            IsEnabled = isEnabled;
            Version = version;
        }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Id))]
        public long Id { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Key))]
        public string Key { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Version))]
        public long Version { get; }
    }

    public sealed class UpdateInstanceModuleDataV1 : BaseData
    {
        public UpdateInstanceModuleDataV1(bool isEnabled, long version)
        {
            IsEnabled = isEnabled;
            Version = version;
        }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Version))]
        public long Version { get; }
    }
}
