using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Account
{
    public static class DevicePairingFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly GuidField PublicId = new();
        public static readonly StringField Code = new(NpgsqlDbType.Varchar) { MaximumLength = 6, MinimumLength = 6, AllowBlank = false, Trim = true };
        public static readonly StringField DeviceName = new(NpgsqlDbType.Varchar) { MaximumLength = 100, AllowBlank = false, Trim = true };
        public static readonly StringField DevicePlatform = new(NpgsqlDbType.Varchar) { MaximumLength = 32, AllowBlank = false, Trim = true };
        public static readonly DateTimeField ExpiresAt = new();
        public static readonly LongField ApprovedAccountId = new() { Minimum = 1, Required = false };
        public static readonly LongField ApprovedProfileId = new() { Minimum = 1, Required = false };
        public static readonly DateTimeField CreatedAt = new();
    }
}
