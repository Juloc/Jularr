using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;
using Jularr.Backend.Data.Fields.Setup;

namespace Jularr.Backend.Data.Admin.Instance.V1
{
    public sealed class SetupModuleV1 : BaseResponse
    {
        public SetupModuleV1(long id, string key, bool isEnabled, long version, bool complete, bool mediaServer, bool requests)
        {
            Id = id;
            Key = key;
            IsEnabled = isEnabled;
            Version = version;
            Complete = complete;
            MediaServer = mediaServer;
            Requests = requests;
        }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Id))]
        public long Id { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Key))]
        public string Key { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Version))]
        public long Version { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.IsEnabled))]
        public bool Complete { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.IsEnabled))]
        public bool MediaServer { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.IsEnabled))]
        public bool Requests { get; }
    }

    public sealed class SetupModuleInputV1 : BaseData
    {
        public SetupModuleInputV1(long id, bool isEnabled, long version)
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

    public sealed class SaveSetupModulesDataV1 : BaseData
    {
        public SaveSetupModulesDataV1(string name, long version, long setupVersion, BaseDataList<SetupModuleInputV1> modules)
        {
            Name = name;
            Version = version;
            SetupVersion = setupVersion;
            Modules = modules;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Name))]
        public string Name { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Version))]
        public long Version { get; }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.Version))]
        public long SetupVersion { get; }
        public BaseDataList<SetupModuleInputV1> Modules { get; }

        public override void Validate()
        {
            base.Validate();
            if (Modules is null || Modules.Count is < 1 or > 64) throw new DataValidationException(nameof(Modules), "out_of_range");
            Modules.Validate();
            if (Modules.Select(module => module.Id).Distinct().Count() != Modules.Count) throw new DataValidationException(nameof(Modules), "duplicate_value");
        }
    }
}
