using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Profile
{
    internal sealed class HomeLayoutSaveData : BaseData
    {
        public HomeLayoutSaveData(long profileId, string[] shelfKeys, string[] hiddenKeys)
        {
            ProfileId = profileId;
            ShelfKeys = shelfKeys;
            HiddenKeys = hiddenKeys;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(HomeShelfFields), nameof(HomeShelfFields.Keys))]
        public string[] ShelfKeys { get; }

        [DataField(typeof(HomeShelfFields), nameof(HomeShelfFields.Keys))]
        public string[] HiddenKeys { get; }
    }
}
