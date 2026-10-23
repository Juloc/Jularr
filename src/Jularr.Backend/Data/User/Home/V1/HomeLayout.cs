using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Home.V1
{
    public sealed class HomeLayoutParametersV1 : BaseParameters
    {
    }

    // One shelf of the profile's home page, listed in its order.
    public sealed class HomeShelfV1 : BaseResponse
    {
        public HomeShelfV1(HomeShelfType homeShelfTypeId, bool isHidden)
        {
            HomeShelfTypeId = homeShelfTypeId;
            IsHidden = isHidden;
        }

        [DataField(typeof(HomeShelfFields), nameof(HomeShelfFields.HomeShelfTypeId))]
        public HomeShelfType HomeShelfTypeId { get; }

        [DataField(typeof(HomeShelfFields), nameof(HomeShelfFields.IsHidden))]
        public bool IsHidden { get; }
    }

    // Every shelf once, in the wanted order; the hidden ones among them.
    public sealed class SaveHomeLayoutDataV1 : BaseData
    {
        public SaveHomeLayoutDataV1(string[] shelfKeys, string[] hiddenKeys)
        {
            ShelfKeys = shelfKeys;
            HiddenKeys = hiddenKeys;
        }

        [DataField(typeof(HomeShelfFields), nameof(HomeShelfFields.Keys))]
        public string[] ShelfKeys { get; }

        [DataField(typeof(HomeShelfFields), nameof(HomeShelfFields.Keys))]
        public string[] HiddenKeys { get; }
    }
}
