using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Admin.Request.V1
{
    public sealed class ReadRequestRulesParametersV1 : BaseParameters
    {
    }

    public sealed class RequestRuleParametersV1 : BaseParameters
    {
        public RequestRuleParametersV1(long requestRuleId)
        {
            RequestRuleId = requestRuleId;
        }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Id))]
        public long RequestRuleId { get; }
    }

    public sealed class DeleteRequestRuleParametersV1 : BaseParameters
    {
        public DeleteRequestRuleParametersV1(long requestRuleId, long version)
        {
            RequestRuleId = requestRuleId;
            Version = version;
        }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Id))]
        public long RequestRuleId { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Version))]
        public long Version { get; }
    }

    // A rule's values; without a limit requests are unlimited, without profiles the defaults apply.
    public sealed class SaveRequestRuleDataV1 : BaseData
    {
        public SaveRequestRuleDataV1(string name, string? description, int? requestLimit, int periodDays, bool isAutomaticApproval, string[] allowedMediaKeys, long[] acquisitionProfileIds,
            bool isDefault, long? version)
        {
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

    public sealed class CreateRequestRuleResultV1 : BaseResponse
    {
        public CreateRequestRuleResultV1(long id)
        {
            Id = id;
        }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Id))]
        public long Id { get; }
    }

    public sealed class RequestRuleDetailsV1 : BaseResponse
    {
        public RequestRuleDetailsV1(long id, string name, string? description, int? requestLimit, int periodDays, bool isAutomaticApproval, string[] allowedMediaKeys,
            long[] acquisitionProfileIds, bool isDefault, long accountCount, long version)
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
            AccountCount = accountCount;
            Version = version;
        }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Id))]
        public long Id { get; }

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

        // Accounts assigned to the rule explicitly.
        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.AccountCount))]
        public long AccountCount { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Version))]
        public long Version { get; }
    }

    public sealed class ReadRequestUsersParametersV1 : BaseParameters
    {
        public ReadRequestUsersParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class AccountRequestRuleParametersV1 : BaseParameters
    {
        public AccountRequestRuleParametersV1(long accountId)
        {
            AccountId = accountId;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long AccountId { get; }
    }

    // An account's resolved rule; inherited without an assignment, overridden where it sets its own values.
    public sealed class AccountRequestRuleV1 : BaseResponse
    {
        public AccountRequestRuleV1(long accountId, string displayName, AccountRoleType accountRoleTypeId, long requestRuleId, string requestRuleName, bool isInherited, bool isOverridden,
            int? requestLimit, int periodDays, bool isAutomaticApproval, string[] allowedMediaKeys, long[] acquisitionProfileIds)
        {
            AccountId = accountId;
            DisplayName = displayName;
            AccountRoleTypeId = accountRoleTypeId;
            RequestRuleId = requestRuleId;
            RequestRuleName = requestRuleName;
            IsInherited = isInherited;
            IsOverridden = isOverridden;
            RequestLimit = requestLimit;
            PeriodDays = periodDays;
            IsAutomaticApproval = isAutomaticApproval;
            AllowedMediaKeys = allowedMediaKeys;
            AcquisitionProfileIds = acquisitionProfileIds;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long AccountId { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.DisplayName))]
        public string DisplayName { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.AccountRoleTypeId))]
        public AccountRoleType AccountRoleTypeId { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Id))]
        public long RequestRuleId { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.Name))]
        public string RequestRuleName { get; }

        [DataField(typeof(RequestRuleFields), nameof(RequestRuleFields.IsInherited))]
        public bool IsInherited { get; }

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

    // Without a rule the default applies; without overriding, the rule's values apply unchanged.
    public sealed class SaveAccountRequestRuleDataV1 : BaseData
    {
        public SaveAccountRequestRuleDataV1(long? requestRuleId, bool isOverridden, int? requestLimit, int periodDays, bool isAutomaticApproval, string[] allowedMediaKeys,
            long[] acquisitionProfileIds)
        {
            RequestRuleId = requestRuleId;
            IsOverridden = isOverridden;
            RequestLimit = requestLimit;
            PeriodDays = periodDays;
            IsAutomaticApproval = isAutomaticApproval;
            AllowedMediaKeys = allowedMediaKeys;
            AcquisitionProfileIds = acquisitionProfileIds;
        }

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
}
