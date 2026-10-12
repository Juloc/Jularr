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
}
