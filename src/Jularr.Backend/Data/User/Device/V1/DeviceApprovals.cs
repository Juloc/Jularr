using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.User.Device.V1
{
    public sealed class ApproveDevicePairingParametersV1 : BaseParameters
    {
    }

    // The device that waits for a code; it names itself.
    public sealed class DevicePairingRequestV1 : BaseResponse
    {
        public DevicePairingRequestV1(long id, string? deviceName, string devicePlatform, string code)
        {
            Id = id;
            DeviceName = deviceName;
            DevicePlatform = devicePlatform;
            Code = code;
        }

        // The confirmation applies to this pairing alone.
        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.Id))]
        public long Id { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.DeviceName))]
        public string? DeviceName { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.DevicePlatform))]
        public string DevicePlatform { get; }

        // To compare with the code the device shows.
        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.Code))]
        public string Code { get; }
    }

    public sealed class InspectDevicePairingDataV1 : BaseData
    {
        public InspectDevicePairingDataV1(string code)
        {
            Code = code;
        }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.Code))]
        public string Code { get; }
    }

    public sealed class ApproveDevicePairingDataV1 : BaseData
    {
        public ApproveDevicePairingDataV1(long id, string code)
        {
            Id = id;
            Code = code;
        }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.Id))]
        public long Id { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.Code))]
        public string Code { get; }
    }
}
