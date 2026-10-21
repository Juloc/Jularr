namespace Jularr.Backend.Data.Fields.Live
{
    public static class LiveFields
    {
        public static readonly LongField BytesPerSecond = new() { Minimum = 0, Required = false };
        public static readonly IntField DownloadCount = new() { Minimum = 0 };
        public static readonly StringField Version = new() { MaximumLength = 64 };
        public static readonly BoolField HasDownloads = new();
        public static readonly BoolField HasJobs = new();
    }
}
