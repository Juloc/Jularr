namespace Jularr.Backend.Data.Fields.Library
{
    public static class SubtitleFields
    {
        public static readonly StringArrayField TrackLanguages = new() { ItemMaximumLength = 16 };
        public static readonly StringArrayField SiblingPaths = new() { ItemMaximumLength = 2048 };
    }
}
