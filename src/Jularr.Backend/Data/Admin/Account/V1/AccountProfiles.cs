using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Admin.Account.V1
{
    public sealed class ReadAccountProfilesParametersV1 : BaseParameters
    {
        public ReadAccountProfilesParametersV1(long accountId, int page = 1, int pageSize = 25)
        {
            AccountId = accountId;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(AccountProfileFields), nameof(AccountProfileFields.AccountId))]
        public long AccountId { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class AccountProfileParametersV1 : BaseParameters
    {
        public AccountProfileParametersV1(long accountId, long profileId)
        {
            AccountId = accountId;
            ProfileId = profileId;
        }

        [DataField(typeof(AccountProfileFields), nameof(AccountProfileFields.AccountId))]
        public long AccountId { get; }

        [DataField(typeof(AccountProfileFields), nameof(AccountProfileFields.ProfileId))]
        public long ProfileId { get; }
    }

    public sealed class AccountProfileDetailsV1 : BaseResponse
    {
        public AccountProfileDetailsV1(long profileId, string displayName, long ownerAccountId, bool hasPin)
        {
            ProfileId = profileId;
            DisplayName = displayName;
            OwnerAccountId = ownerAccountId;
            HasPin = hasPin;
        }

        [DataField(typeof(AccountProfileFields), nameof(AccountProfileFields.ProfileId))]
        public long ProfileId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))]
        public string DisplayName { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.OwnerAccountId))]
        public long OwnerAccountId { get; }

        public bool HasPin { get; }
    }

    public sealed class ProfilePinParametersV1 : BaseParameters
    {
        public ProfilePinParametersV1(long profileId)
        {
            ProfileId = profileId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }
    }
}
