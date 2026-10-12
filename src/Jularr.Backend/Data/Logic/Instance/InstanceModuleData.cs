using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Logic.Instance
{
    internal sealed class InstanceModuleParameters : BaseData
    {
        public InstanceModuleParameters(string[] moduleKeys)
        {
            ModuleKeys = moduleKeys;
        }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.ModuleKeys))]
        public string[] ModuleKeys { get; }
    }

    internal sealed class InstanceModuleData : BaseData
    {
        public InstanceModuleData(string key, bool isEnabled)
        {
            Key = key;
            IsEnabled = isEnabled;
        }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Key))]
        public string Key { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.IsEnabled))]
        public bool IsEnabled { get; }
    }

    internal sealed class InstanceModuleUpdateData : BaseData
    {
        public InstanceModuleUpdateData(long id, bool isEnabled, long version)
        {
            Id = id;
            IsEnabled = isEnabled;
            Version = version;
        }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Id))]
        public long Id { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Version))]
        public long Version { get; }
    }
}
