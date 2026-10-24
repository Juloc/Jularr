namespace Jularr.Backend.Data.Fields.Storage
{
    public static class DownloadStorageFields
    {
        public static readonly LongField ConfiguredJobLimitBytes = new() { Required = false, Minimum = 1 };
        public static readonly EnumField DownloadStorageAreaId = new(typeof(Enums.Storage.DownloadStorageArea));
        public static readonly LongField FreeSpaceReserveBytes = new() { Minimum = 0 };
        public static readonly IntField MaxParallelJobs = new() { Minimum = 1, Maximum = 20 };
        public static readonly StringField FilesystemIdentity = new() { MaximumLength = 128, AllowBlank = false };
        public static readonly BoolField IsConfigured = new();
        public static readonly BoolField Confirmed = new();
        public static readonly GuidField WorkspaceId = new();
        public static readonly LongField CleanupCount = new() { Minimum = 0 };
        public static readonly LongField CleanupBytes = new() { Minimum = 0 };
    }
}
