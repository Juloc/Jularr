using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Data.Fields.Library
{
    public static class LibraryFilterFields
    {
        public static readonly EnumArrayField AvailabilityTypeIds = new(typeof(LibraryAvailabilityType)) { MaximumLength = 3 };
        public static readonly StringArrayField Genres = new() { ItemMaximumLength = 4000, MaximumLength = 100 };
    }
}
