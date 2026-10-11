using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Authentication
{
    public sealed class PlexRegistrationData : BaseData
    {
        public PlexRegistrationData(bool hasLibraryAccess, bool requiresApproval, long uiLocaleId)
        {
            HasLibraryAccess = hasLibraryAccess;
            RequiresApproval = requiresApproval;
            UiLocaleId = uiLocaleId;
        }

        public bool HasLibraryAccess { get; }
        public bool RequiresApproval { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.UiLocaleId))]
        public long UiLocaleId { get; }
    }
}
