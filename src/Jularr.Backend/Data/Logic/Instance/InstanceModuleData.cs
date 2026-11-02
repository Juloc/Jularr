using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Logic.Instance
{
    internal sealed class InstanceModulesUpdateData : BaseData
    {
        public InstanceModulesUpdateData(long[] ids, long[] versions, long[] enabledIds)
        {
            Ids = ids;
            Versions = versions;
            EnabledIds = enabledIds;
        }

        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Ids))]
        public long[] Ids { get; }
        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Ids))]
        public long[] Versions { get; }
        [DataField(typeof(InstanceModuleFields), nameof(InstanceModuleFields.Ids))]
        public long[] EnabledIds { get; }
    }

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

    // The Caching module's settings, saved with the switches: days unused extended data of titles outside the library is kept, and whether Discover's lists are detailed in advance.
    internal sealed class CachingSettingsData : BaseData
    {
        public CachingSettingsData(int catalogDetailKeepDays, bool detailsDiscoverAhead)
        {
            CatalogDetailKeepDays = catalogDetailKeepDays;
            DetailsDiscoverAhead = detailsDiscoverAhead;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.CatalogDetailKeepDays))]
        public int CatalogDetailKeepDays { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.DetailsDiscoverAhead))]
        public bool DetailsDiscoverAhead { get; }
    }
}
