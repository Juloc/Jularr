using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    internal sealed class RequestRuleSaveData : BaseData
    {
        public RequestRuleSaveData(long? id, string name, string? description, int? requestLimit, int periodDays, bool isAutomaticApproval, string[] allowedMediaKeys,
            long[] acquisitionProfileIds, bool isDefault, long? version)
        {
            Id = id;
            Name = name;
            Description = description;
            RequestLimit = requestLimit;
            PeriodDays = periodDays;
            IsAutomaticApproval = isAutomaticApproval;
            AllowedMediaKeys = allowedMediaKeys;
            AcquisitionProfileIds = acquisitionProfileIds;
            IsDefault = isDefault;
            Version = version;
        }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Id), false)]
        public long? Id { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Name))]
        public string Name { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Description))]
        public string? Description { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.RequestLimit))]
        public int? RequestLimit { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.PeriodDays))]
        public int PeriodDays { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.IsAutomaticApproval))]
        public bool IsAutomaticApproval { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.MediaKeys))]
        public string[] AllowedMediaKeys { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.AcquisitionProfileIds))]
        public long[] AcquisitionProfileIds { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.IsDefault))]
        public bool IsDefault { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Version), false)]
        public long? Version { get; }
    }

    internal sealed class RequestRuleKeyData : BaseData
    {
        public RequestRuleKeyData(long id, long version)
        {
            Id = id;
            Version = version;
        }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Id))]
        public long Id { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Version))]
        public long Version { get; }
    }

    internal sealed class RequestRuleStateData : BaseData
    {
        public RequestRuleStateData(bool isDefault)
        {
            IsDefault = isDefault;
        }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.IsDefault))]
        public bool IsDefault { get; }
    }

    internal sealed class RequestRuleReferenceData : BaseData
    {
        public RequestRuleReferenceData(long? id, string? name, string[] allowedMediaKeys, long[] acquisitionProfileIds)
        {
            Id = id;
            Name = name;
            AllowedMediaKeys = allowedMediaKeys;
            AcquisitionProfileIds = acquisitionProfileIds;
        }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Id), false)]
        public long? Id { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Name), false)]
        public string? Name { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.MediaKeys))]
        public string[] AllowedMediaKeys { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.AcquisitionProfileIds))]
        public long[] AcquisitionProfileIds { get; }
    }

    // Whether a rule's name is free and its media and profiles exist.
    internal sealed class RequestRuleCheckData : BaseData
    {
        public RequestRuleCheckData(bool isNameTaken, bool areMediaKeysKnown, bool areProfilesKnown)
        {
            IsNameTaken = isNameTaken;
            AreMediaKeysKnown = areMediaKeysKnown;
            AreProfilesKnown = areProfilesKnown;
        }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.IsNameTaken))]
        public bool IsNameTaken { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.AreMediaKeysKnown))]
        public bool AreMediaKeysKnown { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.AreProfilesKnown))]
        public bool AreProfilesKnown { get; }
    }

    internal sealed class AccountRequestRuleSaveData : BaseData
    {
        public AccountRequestRuleSaveData(long accountId, long? requestRuleId, bool isOverridden, int? requestLimit, int periodDays, bool isAutomaticApproval, string[] allowedMediaKeys,
            long[] acquisitionProfileIds)
        {
            AccountId = accountId;
            RequestRuleId = requestRuleId;
            IsOverridden = isOverridden;
            RequestLimit = requestLimit;
            PeriodDays = periodDays;
            IsAutomaticApproval = isAutomaticApproval;
            AllowedMediaKeys = allowedMediaKeys;
            AcquisitionProfileIds = acquisitionProfileIds;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long AccountId { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Id), false)]
        public long? RequestRuleId { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.IsOverridden))]
        public bool IsOverridden { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.RequestLimit))]
        public int? RequestLimit { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.PeriodDays))]
        public int PeriodDays { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.IsAutomaticApproval))]
        public bool IsAutomaticApproval { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.MediaKeys))]
        public string[] AllowedMediaKeys { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.AcquisitionProfileIds))]
        public long[] AcquisitionProfileIds { get; }
    }

    // What the requesting account's rule says about one request.
    internal sealed class RequestPolicyData : BaseData
    {
        public RequestPolicyData(int? requestLimit, bool isAutomaticApproval, bool isBlocked, bool isProfileAllowed, long recentCount, long? activeRequestId)
        {
            RequestLimit = requestLimit;
            IsAutomaticApproval = isAutomaticApproval;
            IsBlocked = isBlocked;
            IsProfileAllowed = isProfileAllowed;
            RecentCount = recentCount;
            ActiveRequestId = activeRequestId;
        }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.RequestLimit))]
        public int? RequestLimit { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.IsAutomaticApproval))]
        public bool IsAutomaticApproval { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.IsBlocked))]
        public bool IsBlocked { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.IsProfileAllowed))]
        public bool IsProfileAllowed { get; }

        // Requests the account made within its rule's period.
        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.RecentCount))]
        public long RecentCount { get; }

        [DataField(typeof(AcquisitionRequestFields), nameof(AcquisitionRequestFields.Id), false)]
        public long? ActiveRequestId { get; }
    }
}
