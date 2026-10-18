using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

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

    // Public provider facts only. Profile-specific state is overlaid after reading.
    internal sealed class DiscoverySnapshotData : BaseData
    {
        public DiscoverySnapshotData(string key, string providerKey, string payload, DateTime fetchedAt, DateTime freshUntil, DateTime retainUntil)
        {
            Key = key;
            ProviderKey = providerKey;
            Payload = payload;
            FetchedAt = fetchedAt;
            FreshUntil = freshUntil;
            RetainUntil = retainUntil;
        }

        [DataField(typeof(DiscoverySnapshotFields), nameof(DiscoverySnapshotFields.Key))]
        public string Key { get; }

        [DataField(typeof(DiscoverySnapshotFields), nameof(DiscoverySnapshotFields.ProviderKey))]
        public string ProviderKey { get; }

        [DataField(typeof(DiscoverySnapshotFields), nameof(DiscoverySnapshotFields.Payload))]
        public string Payload { get; }

        [DataField(typeof(DiscoverySnapshotFields), nameof(DiscoverySnapshotFields.FetchedAt))]
        public DateTime FetchedAt { get; }

        [DataField(typeof(DiscoverySnapshotFields), nameof(DiscoverySnapshotFields.FreshUntil))]
        public DateTime FreshUntil { get; }

        [DataField(typeof(DiscoverySnapshotFields), nameof(DiscoverySnapshotFields.RetainUntil))]
        public DateTime RetainUntil { get; }
    }
}
