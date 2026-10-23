using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class HomeShelfFields
    {
        public static readonly EnumField HomeShelfTypeId = new(typeof(HomeShelfType));
        public static readonly BoolField IsHidden = new();
        public static readonly StringArrayField Keys = new() { ItemMaximumLength = 32, MaximumLength = 32 };
    }
}
