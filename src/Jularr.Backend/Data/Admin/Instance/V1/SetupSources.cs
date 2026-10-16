using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Provider;
using Jularr.Backend.Data.Fields.Setup;

namespace Jularr.Backend.Data.Admin.Instance.V1
{
    public sealed class SetupSourcesDetailsV1 : BaseResponse
    {
        public SetupSourcesDetailsV1(bool hasTmdbKey)
        {
            HasTmdbKey = hasTmdbKey;
        }

        [DataField(typeof(ProviderCredentialFields), nameof(ProviderCredentialFields.HasSecret))]
        public bool HasTmdbKey { get; }
    }

    public sealed class SaveSetupSourcesDataV1 : BaseData
    {
        public SaveSetupSourcesDataV1(long version, string? tmdbKey)
        {
            Version = version;
            TmdbKey = tmdbKey;
        }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.Version))]
        public long Version { get; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
        [DataField(typeof(ProviderCredentialFields), nameof(ProviderCredentialFields.Secret))]
        public string? TmdbKey { get; }
    }
}
