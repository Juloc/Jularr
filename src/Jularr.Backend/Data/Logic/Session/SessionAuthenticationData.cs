using Jularr.Data.Common;
using Jularr.Data.Fields.Account;
using Jularr.Data.Fields.Profile;

namespace Jularr.Data.Logic.Session;

public sealed class SessionAuthenticationData : BaseData
{
    public SessionAuthenticationData(long accountId, long profileId, long profileOwnerAccountId)
    {
        AccountId = accountId;
        ProfileId = profileId;
        ProfileOwnerAccountId = profileOwnerAccountId;
    }

    [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
    public long AccountId { get; }

    [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
    public long ProfileId { get; }

    [DataField(typeof(ProfileFields), nameof(ProfileFields.OwnerAccountId))]
    public long ProfileOwnerAccountId { get; }
}
