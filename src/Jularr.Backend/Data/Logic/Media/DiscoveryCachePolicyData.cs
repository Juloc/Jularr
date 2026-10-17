using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class DiscoveryCachePolicyData : BaseData
    {
        public DiscoveryCachePolicyData(int metadataTrendingHours, int metadataRatingsHours, int metadataFreshDays, int discoveryCacheEntries, int discoveryCacheMegabytes)
        {
            MetadataTrendingHours = metadataTrendingHours;
            MetadataRatingsHours = metadataRatingsHours;
            MetadataFreshDays = metadataFreshDays;
            DiscoveryCacheEntries = discoveryCacheEntries;
            DiscoveryCacheMegabytes = discoveryCacheMegabytes;
        }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.MetadataTrendingHours))]
        public int MetadataTrendingHours { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.MetadataRatingsHours))]
        public int MetadataRatingsHours { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.MetadataFreshDays))]
        public int MetadataFreshDays { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.DiscoveryCacheEntries))]
        public int DiscoveryCacheEntries { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.DiscoveryCacheMegabytes))]
        public int DiscoveryCacheMegabytes { get; }

    }
}
