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
        public ProviderSecretData(long id, byte[]? protectedSecret)
        {
            Id = id;
            ProtectedSecret = protectedSecret;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Id))]
        public long Id { get; }

        [JsonIgnore]
        [DataField(typeof(ProviderFields), nameof(ProviderFields.ProtectedSecret))]
        public byte[]? ProtectedSecret { get; }
    }

    internal sealed class ProviderCredentialData : BaseData
    {
        public ProviderCredentialData(long id, bool isEnabled, byte[]? protectedSecret)
        {
            Id = id;
            IsEnabled = isEnabled;
            ProtectedSecret = protectedSecret;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Id))]
        public long Id { get; }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.IsEnabled))]
        public bool IsEnabled { get; }

        [JsonIgnore]
        [DataField(typeof(ProviderFields), nameof(ProviderFields.ProtectedSecret))]
        public byte[]? ProtectedSecret { get; }
    }
}
