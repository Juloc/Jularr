using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Logic.Session
{
    internal sealed class DevicePairingStartData : BaseData
    {
        public DevicePairingStartData(string code, string deviceName, string devicePlatform)
        {
            Code = code;
            DeviceName = deviceName;
            DevicePlatform = devicePlatform;
        }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.Code))]
        public string Code { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.DeviceName))]
        public string DeviceName { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.DevicePlatform))]
        public string DevicePlatform { get; }
    }

    internal sealed class DevicePairingCodeData : BaseData
    {
        public DevicePairingCodeData(string code)
        {
            Code = code;
        }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.Code))]
        public string Code { get; }
    }

    // What a device calls itself; it names itself, nothing verifies it.
    internal sealed class DevicePairingDeviceData : BaseData
    {
        public DevicePairingDeviceData(string deviceName, string devicePlatform)
        {
            DeviceName = deviceName;
            DevicePlatform = devicePlatform;
        }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.DeviceName))]
        public string DeviceName { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.DevicePlatform))]
        public string DevicePlatform { get; }
    }

    internal sealed class DevicePairingStartedData : BaseData
    {
        public DevicePairingStartedData(Guid publicId, string code, DateTime expiresAt)
        {
            PublicId = publicId;
            Code = code;
            ExpiresAt = expiresAt;
        }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.PublicId))]
        public Guid PublicId { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.Code))]
        public string Code { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.ExpiresAt))]
        public DateTime ExpiresAt { get; }
    }

    internal sealed class DevicePairingParameters : BaseData
    {
        public DevicePairingParameters(Guid publicId)
        {
            PublicId = publicId;
        }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.PublicId))]
        public Guid PublicId { get; }
    }

    // A pairing as the device fetches it; approved ones name the account and profile.
    internal sealed class DevicePairingStateData : BaseData
    {
        public DevicePairingStateData(long id, string deviceName, string devicePlatform, long? approvedAccountId, long? approvedProfileId)
        {
            Id = id;
            DeviceName = deviceName;
            DevicePlatform = devicePlatform;
            ApprovedAccountId = approvedAccountId;
            ApprovedProfileId = approvedProfileId;
        }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.Id))]
        public long Id { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.DeviceName))]
        public string DeviceName { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.DevicePlatform))]
        public string DevicePlatform { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.ApprovedAccountId))]
        public long? ApprovedAccountId { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.ApprovedProfileId))]
        public long? ApprovedProfileId { get; }
    }

    internal sealed class DevicePairingApprovalData : BaseData
    {
        public DevicePairingApprovalData(string code, long approvedAccountId, long approvedProfileId)
        {
            Code = code;
            ApprovedAccountId = approvedAccountId;
            ApprovedProfileId = approvedProfileId;
        }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.Code))]
        public string Code { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.ApprovedAccountId), true)]
        public long ApprovedAccountId { get; }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.ApprovedProfileId), true)]
        public long ApprovedProfileId { get; }
    }

    internal sealed class DeviceSessionData : BaseData
    {
        public DeviceSessionData(Guid publicId, long accountId, long profileId, byte[] tokenHash, DateTime expiresAt, string deviceName, string devicePlatform)
        {
            PublicId = publicId;
            AccountId = accountId;
            ProfileId = profileId;
            TokenHash = tokenHash;
            ExpiresAt = expiresAt;
            DeviceName = deviceName;
            DevicePlatform = devicePlatform;
        }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.PublicId))]
        public Guid PublicId { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.AccountId))]
        public long AccountId { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.ActiveProfileId), true)]
        public long ProfileId { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.TokenHash))]
        public byte[] TokenHash { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.ExpiresAt))]
        public DateTime ExpiresAt { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.DeviceName), true)]
        public string DeviceName { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.DevicePlatform), true)]
        public string DevicePlatform { get; }
    }

    internal sealed class DeviceSessionRevisionData : BaseData
    {
        public DeviceSessionRevisionData(long credentialRevision)
        {
            CredentialRevision = credentialRevision;
        }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.CredentialRevision))]
        public long CredentialRevision { get; }
    }

    internal sealed class DevicePairingIdData : BaseData
    {
        public DevicePairingIdData(long id)
        {
            Id = id;
        }

        [DataField(typeof(DevicePairingFields), nameof(DevicePairingFields.Id))]
        public long Id { get; }
    }
}
