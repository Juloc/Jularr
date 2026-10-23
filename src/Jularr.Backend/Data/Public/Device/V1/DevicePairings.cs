using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Logic.Session;

namespace Jularr.Backend.Data.Public.Device.V1
{
    public sealed class StartDevicePairingParametersV1 : BaseParameters
    {
    }

    public sealed class StartDevicePairingDataV1 : BaseData
    {
        // Without its own name a device is shown with the default name in the reader's language.
        public StartDevicePairingDataV1(string? deviceName, string devicePlatform, Guid? replacesPairingId = null)
        {
            DeviceName = deviceName;
            DevicePlatform = devicePlatform;
            ReplacesPairingId = replacesPairingId;
        }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.DeviceName))]
        public string? DeviceName { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.DevicePlatform))]
        public string DevicePlatform { get; }

        // The device's previous, unconfirmed pairing; its code ends at once.
        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.PublicId), false)]
        public Guid? ReplacesPairingId { get; }
    }

    // The device shows the code; the pairing id is its secret for fetching the session.
    public sealed class DevicePairingStartedV1 : BaseResponse
    {
        public DevicePairingStartedV1(Guid pairingId, string code, DateTime expiresAt)
        {
            PairingId = pairingId;
            Code = code;
            ExpiresAt = expiresAt;
        }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.PublicId))]
        public Guid PairingId { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.Code))]
        public string Code { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.ExpiresAt))]
        public DateTime ExpiresAt { get; }
    }

    public sealed class ClaimDevicePairingParametersV1 : BaseParameters
    {
        public ClaimDevicePairingParametersV1(Guid pairingId)
        {
            PairingId = pairingId;
        }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.PublicId))]
        public Guid PairingId { get; }
    }

    // Not yet confirmed, or confirmed with the device's session.
    public sealed class DevicePairingClaimV1 : BaseResponse, ISessionResult
    {
        public DevicePairingClaimV1(bool isPaired, SessionCredentialsData? session)
        {
            IsPaired = isPaired;
            Session = session;
        }

        public bool IsPaired { get; }

        [JsonIgnore]
        public SessionCredentialsData? Session { get; }
    }
}
