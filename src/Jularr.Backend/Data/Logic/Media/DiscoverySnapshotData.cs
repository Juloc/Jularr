using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Instance;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class DiscoverySnapshotKeyData : BaseData
    {
        public DiscoverySnapshotKeyData(string key)
        {
            Key = key;
        }
        [DataField(typeof(DiscoverySnapshotFields), nameof(DiscoverySnapshotFields.Key))]
        public string Key { get; }
    }

    internal sealed class DiscoverySnapshotWriteData : BaseData
    {
        public DiscoverySnapshotWriteData(string key, string request, string payload, int freshHours, int discoveryCacheEntries, int discoveryCacheMegabytes)
        {
            Key = key;
            Request = request;
            Payload = payload;
            FreshHours = freshHours;
            DiscoveryCacheEntries = discoveryCacheEntries;
            DiscoveryCacheMegabytes = discoveryCacheMegabytes;
        }
        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.DiscoveryCacheEntries))]
        public int DiscoveryCacheEntries { get; }

        [DataField(typeof(InstanceSettingsFields), nameof(InstanceSettingsFields.DiscoveryCacheMegabytes))]
        public int DiscoveryCacheMegabytes { get; }

        [DataField(typeof(DiscoverySnapshotFields), nameof(DiscoverySnapshotFields.Key))]
        public string Key { get; }
        [DataField(typeof(DiscoverySnapshotFields), nameof(DiscoverySnapshotFields.Request))]
        public string Request { get; }
        [DataField(typeof(DiscoverySnapshotFields), nameof(DiscoverySnapshotFields.Payload))]
        public string Payload { get; }
        [DataField(typeof(DiscoverySnapshotFields), nameof(DiscoverySnapshotFields.FreshHours))]
        public int FreshHours { get; }
    }

    internal sealed class DiscoverySnapshotData : BaseData
    {
        public DiscoverySnapshotData(string key, string request, string payload, bool isFresh)
        {
            Key = key;
            Request = request;
            Payload = payload;
            IsFresh = isFresh;
        }
        [DataField(typeof(DiscoverySnapshotFields), nameof(DiscoverySnapshotFields.Key))]
        public string Key { get; }
        [DataField(typeof(DiscoverySnapshotFields), nameof(DiscoverySnapshotFields.Request))]
        public string Request { get; }
        [DataField(typeof(DiscoverySnapshotFields), nameof(DiscoverySnapshotFields.Payload))]
        public string Payload { get; }
        [DataField(typeof(DiscoverySnapshotFields), nameof(DiscoverySnapshotFields.IsFresh))]
        public bool IsFresh { get; }
    }
}
