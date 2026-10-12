using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class DiscoverFields
    {
        public static readonly EnumField DiscoverFeedTypeId = new(typeof(DiscoverFeedType));
        public static readonly EnumField MediaStateTypeId = new(typeof(MediaStateType));
        public static readonly EnumField LibrarySortTypeId = new(typeof(LibrarySortType));
        public static readonly IntField Page = new() { Minimum = 1, Maximum = 500 };
        public static readonly LongField PositionMs = new() { Minimum = 0 };
        public static readonly LongField DurationMs = new() { Minimum = 0, Required = false };
    }
}
