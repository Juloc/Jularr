using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Provider;

namespace Jularr.Backend.Data.Logic.Provider
{
    internal sealed class ProviderUpdateData : BaseData
    {
        public ProviderUpdateData(long id, bool isEnabled, bool expectedIsEnabled)
        {
            Id = id;
            IsEnabled = isEnabled;
            ExpectedIsEnabled = expectedIsEnabled;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Id))]
        public long Id { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.IsEnabled))]
        public bool ExpectedIsEnabled { get; }
    }

    internal sealed class ProviderSecretData : BaseData
    {
        public ProviderSecretData(long id, byte[]? protectedSecret, string? externalId)
        {
            Id = id;
            ProtectedSecret = protectedSecret;
            ExternalId = externalId;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Id))]
        public long Id { get; }

        [JsonIgnore]
        [DataField(typeof(ProviderFields), nameof(ProviderFields.ProtectedSecret))]
        public byte[]? ProtectedSecret { get; }

        // The public application id of providers that pair it with the secret.
        [DataField(typeof(ProviderFields), nameof(ProviderFields.ExternalId))]
        public string? ExternalId { get; }
    }

    internal sealed class ProviderCredentialData : BaseData
    {
        public ProviderCredentialData(long id, bool isEnabled, byte[]? protectedSecret, string? externalId)
        {
            Id = id;
            IsEnabled = isEnabled;
            ProtectedSecret = protectedSecret;
            ExternalId = externalId;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Id))]
        public long Id { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.IsEnabled))]
        public bool IsEnabled { get; }

        [JsonIgnore]
        [DataField(typeof(ProviderFields), nameof(ProviderFields.ProtectedSecret))]
        public byte[]? ProtectedSecret { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.ExternalId))]
        public string? ExternalId { get; }
    }
}
