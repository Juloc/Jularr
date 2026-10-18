namespace Jularr.Backend.Data.Fields.Storage
{
    public static class DownloadStorageFields
    {
        public static readonly StringField Path = new() { MaximumLength = 4096, Required = false };
        public static readonly LongField ConfiguredJobLimitBytes = new() { Required = false, Minimum = 1 };
        public static readonly BoolField AllowsHostFilesystem = new();
    }
}
