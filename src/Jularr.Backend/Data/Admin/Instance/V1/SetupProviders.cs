using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Provider;
using Jularr.Backend.Data.Fields.Setup;

namespace Jularr.Backend.Data.Admin.Instance.V1
{
    // The providers of the setup step saved together: whether each is on and, where one was typed, its key.
    public sealed class SaveSetupProvidersDataV1 : BaseData
    {
        public SaveSetupProvidersDataV1(long version, BaseDataList<SetupProviderInputV1> providers)
        {
            Version = version;
            Providers = providers;
        }

        [DataField(typeof(InstanceSetupStateFields), nameof(InstanceSetupStateFields.Version))]
        public long Version { get; }

        public BaseDataList<SetupProviderInputV1> Providers { get; }

        public override void Validate()
        {
            base.Validate();
            if (Providers is null || Providers.Count > 32) throw new DataValidationException(nameof(Providers), "out_of_range");
            Providers.Validate();
            if (Providers.Select(provider => provider.Key).Distinct(StringComparer.Ordinal).Count() != Providers.Count) throw new DataValidationException(nameof(Providers), "duplicate_value");
        }
    }

    // One provider by its key. A blank key leaves the stored one as it is; a typed one is stored protected and switches the provider on.
    public sealed class SetupProviderInputV1 : BaseData
    {
        public SetupProviderInputV1(string key, bool isEnabled, string? secret, string? clientId)
        {
            Key = key;
            IsEnabled = isEnabled;
            Secret = secret;
            ClientId = clientId;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Key))]
        public string Key { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.IsEnabled))]
        public bool IsEnabled { get; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
        [DataField(typeof(ProviderCredentialFields), nameof(ProviderCredentialFields.Secret))]
        public string? Secret { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.ExternalId))]
        public string? ClientId { get; }
    }
}
