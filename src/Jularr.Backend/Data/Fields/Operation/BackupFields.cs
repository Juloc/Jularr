using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Operation
{
    public static class BackupFields
    {
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 32, AllowBlank = false };
        public static readonly DateTimeField CreatedAt = new();
        public static readonly LongField SizeBytes = new() { Minimum = 0 };
        public static readonly BoolField IsCompatible = new();
        public static readonly BoolField IsSealed = new();
        public static readonly StringField Passphrase = new() { MaximumLength = 1024, Required = false };
        public static readonly BoolField IsConfigured = new();
        public static readonly BoolField IsRunning = new();
        public static readonly BoolField CanRestore = new();
        public static readonly BoolField HasRecentAttempt = new();
        public static readonly BoolField IsForced = new();
    }
}
