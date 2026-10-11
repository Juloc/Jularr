using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Provider;

namespace Jularr.Backend.Data.Logic.Authentication
{
    public sealed class ExternalLoginParameters : BaseData
    {
        public ExternalLoginParameters(long providerId, string externalAccountId)
        {
            ProviderId = providerId;
            ExternalAccountId = externalAccountId;
        }

        [DataField(typeof(AccountExternalLoginFields), nameof(AccountExternalLoginFields.ProviderId))]
        public long ProviderId { get; }

        [DataField(typeof(AccountExternalLoginFields), nameof(AccountExternalLoginFields.ExternalAccountId))]
        public string ExternalAccountId { get; }
    }

    public sealed class PlexProviderData : BaseData
    {
        public PlexProviderData(long id)
        {
            Id = id;
        }

        [DataField(typeof(ProviderFields), nameof(ProviderFields.Id))]
        public long Id { get; }
    }

    public sealed class AuthChallengeIdData : BaseData
    {
        public AuthChallengeIdData(long id)
        {
            Id = id;
        }

        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.Id))]
        public long Id { get; }
    }
}
