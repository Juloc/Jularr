using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Provider;

namespace Jularr.Backend.Data.Logic.Provider
{
    public sealed class ProviderParameters : BaseData
    {
        public ProviderParameters(string providerKey)
        {
            ProviderKey = providerKey;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Key))]
        public string ProviderKey { get; }
    }

    public sealed class ProviderData : BaseData
    {
        public ProviderData(long id)
        {
            Id = id;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Id))]
        public long Id { get; }
    }
}
