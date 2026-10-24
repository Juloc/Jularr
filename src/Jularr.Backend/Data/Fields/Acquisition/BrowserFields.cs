using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Acquisition
{
    public static class BrowserFields
    {
        public static readonly StringField Path = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false, Trim = true };
        public static readonly StringField RemoteAddress = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false, Trim = true };
        public static readonly StringField ProfilePath = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Trim = true };
        public static readonly BoolField IsAvailable = new();
        public static readonly BoolField IsRunning = new();
        public static readonly StringField Version = new(NpgsqlDbType.Varchar) { MaximumLength = 128, Required = false, Trim = true };
        public static readonly StringField LastUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false, Trim = true };
        public static readonly StringField LastOutcome = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false, Trim = true };
        public static readonly StringField LastError = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false, Trim = true };
        public static readonly StringField InstallationState = new(NpgsqlDbType.Varchar) { MaximumLength = 32, Trim = true };
        public static readonly StringField InstallationError = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false, Trim = true };
        public static readonly LongField DownloadedBytes = new() { Minimum = 0 };
        public static readonly LongField TotalBytes = new() { Minimum = 0 };
        public static readonly StringField InstalledVersion = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false, Trim = true };
        public static readonly LongField ManagedSizeBytes = new() { Minimum = 0 };
        public static readonly DateTimeField LastAttemptAt = new() { Required = false };
        public static readonly DateTimeField At = new();
        public static readonly StringField Kind = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Trim = true };
        public static readonly StringField Message = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Trim = true };
        public static readonly StringField Action = new(NpgsqlDbType.Varchar) { MaximumLength = 32, Trim = true };
        public static readonly StringField Url = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false, Trim = true };
    }
}
