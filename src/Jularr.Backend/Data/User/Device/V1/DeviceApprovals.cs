using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.User.Device.V1
{
    public sealed class ApproveDevicePairingParametersV1 : BaseParameters
    {
    }

    public sealed class ApproveDevicePairingDataV1 : BaseData
    {
        public ApproveDevicePairingDataV1(string code)
        {
            Code = code;
        }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.Code))]
        public string Code { get; }
    }
}
