using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Provider;
using Jularr.Backend.Data.Fields.Provider;

namespace Jularr.Backend.Data.System.Provider.V1
{
    // One provider of the provider sync.
    public sealed class ProviderSyncParametersV1 : BaseParameters
    {
        public ProviderSyncParametersV1(ProviderSyncType providerSyncTypeId)
        {
            ProviderSyncTypeId = providerSyncTypeId;
        }

        [DataField(typeof(ProviderSyncFields), nameof(ProviderSyncFields.ProviderSyncTypeId))]
        public ProviderSyncType ProviderSyncTypeId { get; }
    }
}
