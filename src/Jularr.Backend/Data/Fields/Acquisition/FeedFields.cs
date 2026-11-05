namespace Jularr.Backend.Data.Fields.Acquisition
{
    public static class FeedFields
    {
        public static readonly StringArrayField ReleaseKeys = new() { ItemMaximumLength = 600, MaximumLength = 1000 };
        public static readonly BoolField HasSeen = new();
        public static readonly DateTimeField SeenBefore = new();
    }
}
