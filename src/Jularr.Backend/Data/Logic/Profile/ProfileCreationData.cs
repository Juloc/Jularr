using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Profile
{
    public sealed class ProfileCreationData : BaseData
    {
        public ProfileCreationData(long ownerAccountId, string displayName, long uiLocaleId)
        {
            OwnerAccountId = ownerAccountId;
            DisplayName = displayName;
            UiLocaleId = uiLocaleId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.OwnerAccountId))]
        public long OwnerAccountId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))]
        public string DisplayName { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.UiLocaleId))]
        public long UiLocaleId { get; }
    }
}
