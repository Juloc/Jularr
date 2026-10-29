using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Admin.Instance.V1
{
    public sealed class ReadInstanceModulesParametersV1 : BaseParameters
    {
    }

    public sealed class UpdateInstanceModulesParametersV1 : BaseParameters
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
        public InstanceModuleDetailsV1(long id, string key, bool isEnabled, long version, string[] requires)
        {
            Id = id;
            Key = key;
            IsEnabled = isEnabled;
            Version = version;
            Requires = requires;
        }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Id))]
        public long Id { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Key))]
        public string Key { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Version))]
        public long Version { get; }

        // The modules that must be on while this one is.
        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Requires))]
        public string[] Requires { get; }
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

    public sealed class InstanceModuleInputV1 : BaseData
    {
        public InstanceModuleInputV1(long id, bool isEnabled, long version)
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

    // The Caching module's settings as saved; they are saved with the switches (UpdateInstanceModulesDataV1).
    public sealed class CachingSettingsV1 : BaseResponse
    {
        public CachingSettingsV1(int catalogDetailKeepDays, bool detailsDiscoverAhead)
        {
            CatalogDetailKeepDays = catalogDetailKeepDays;
            DetailsDiscoverAhead = detailsDiscoverAhead;
        }

        // Days unused extended data of titles outside the library is kept.
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CatalogDetailKeepDays))]
        public int CatalogDetailKeepDays { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.DetailsDiscoverAhead))]
        public bool DetailsDiscoverAhead { get; }
    }

    // Every module as shown with its new state and the Caching module's settings; the save is one step that must leave the switches consistent.
    public sealed class UpdateInstanceModulesDataV1 : BaseData
    {
        public UpdateInstanceModulesDataV1(BaseDataList<InstanceModuleInputV1> modules, int catalogDetailKeepDays, bool detailsDiscoverAhead)
        {
            Modules = modules;
            CatalogDetailKeepDays = catalogDetailKeepDays;
            DetailsDiscoverAhead = detailsDiscoverAhead;
        }

        public BaseDataList<InstanceModuleInputV1> Modules { get; }

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
        }
    }
}
