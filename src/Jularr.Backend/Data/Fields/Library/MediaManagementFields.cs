using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Data.Fields.Library
{
    public static class MediaManagementFields
    {
        public static readonly EnumField MediaTypeId = new(typeof(MediaType));
        public static readonly BoolField UsesHardlinks = new();
        public static readonly BoolField SkipsFreeSpaceCheck = new();
        public static readonly LongField MinimumFreeSpaceMegabytes = new() { Minimum = 0, Maximum = int.MaxValue };
        public static readonly BoolField AnalysesMedia = new();
        public static readonly LongField Version = new() { Minimum = 1 };
    }
}
