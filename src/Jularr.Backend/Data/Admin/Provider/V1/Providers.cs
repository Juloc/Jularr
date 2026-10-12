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
        public ProviderDetailsV1(long id, string key, bool isEnabled)
        {
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
