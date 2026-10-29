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

    public sealed class SaveSetupModulesDataV1 : BaseData
    {
        public SaveSetupModulesDataV1(string name, long version, long setupVersion, BaseDataList<InstanceModuleInputV1> modules, BaseDataList<UpdateInstanceMediaDataV1> media,
            int catalogDetailKeepDays, bool detailsDiscoverAhead)
        {
            Name = name;
            Version = version;
            SetupVersion = setupVersion;
            Modules = modules;
            Media = media;
            CatalogDetailKeepDays = catalogDetailKeepDays;
            DetailsDiscoverAhead = detailsDiscoverAhead;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Name))]
        public string Name { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.Version))]
        public long Version { get; }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.Version))]
        public long SetupVersion { get; }
        public BaseDataList<InstanceModuleInputV1> Modules { get; }

        // The media types and subtypes the instance offers, saved with the modules; types before the subtypes that need them.
        public BaseDataList<UpdateInstanceMediaDataV1> Media { get; }

        // The Caching module's settings, saved with the switches.
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CatalogDetailKeepDays))]
        public int CatalogDetailKeepDays { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.DetailsDiscoverAhead))]
        public bool DetailsDiscoverAhead { get; }

        public override void Validate()
        {
            base.Validate();
            if (Modules is null || Modules.Count is < 1 or > 64) throw new DataValidationException(nameof(Modules), "out_of_range");
            Modules.Validate();
            if (Modules.Select(module => module.Id).Distinct().Count() != Modules.Count) throw new DataValidationException(nameof(Modules), "duplicate_value");
            if (Media is null || Media.Count > 16) throw new DataValidationException(nameof(Media), "out_of_range");
            Media.Validate();
            if (Media.Select(item => item.Key).Distinct().Count() != Media.Count) throw new DataValidationException(nameof(Media), "duplicate_value");
        }
    }
}
