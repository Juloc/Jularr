using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Provider;

namespace Jularr.Backend.Data.Admin.Provider.V1
{
    public sealed class ReadProvidersParametersV1 : BaseParameters
    {
        public ReadProvidersParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class UpdateProviderParametersV1 : BaseParameters
    {
        public UpdateProviderParametersV1(long providerId)
        {
            ProviderId = providerId;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Id))]
        public long ProviderId { get; }
    }

    public sealed class ProviderDetailsV1 : BaseResponse
    {
        public ProviderDetailsV1(long id, string key, bool isEnabled, bool hasSecret)
        {
            HasSecret = hasSecret;
            Id = id;
            Key = key;
            IsEnabled = isEnabled;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Id))]
        public long Id { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Key))]
        public string Key { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(ProviderCredentialFields), nameof(ProviderCredentialFields.HasSecret))]
        public bool HasSecret { get; }
    }

    public sealed class SaveProviderSecretDataV1 : BaseData
    {
        public SaveProviderSecretDataV1(string? secret, string? clientId = null)
        {
            Secret = secret;
            ClientId = clientId;
        }

        [DataField(typeof(ProviderCredentialFields), nameof(ProviderCredentialFields.Secret))]
        public string? Secret { get; }

        // IGDB pairs the secret with the Twitch application's public id; empty keeps the stored one.
        [DataField(typeof(ProviderFields), nameof(ProviderFields.ExternalId))]
        public string? ClientId { get; }
    }

    public sealed class UpdateProviderDataV1 : BaseData
    {
        public UpdateProviderDataV1(bool isEnabled, bool expectedIsEnabled)
        {
            IsEnabled = isEnabled;
            ExpectedIsEnabled = expectedIsEnabled;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.IsEnabled))]
        public bool ExpectedIsEnabled { get; }
    }
}
