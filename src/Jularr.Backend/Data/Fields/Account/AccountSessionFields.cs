using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Account
{
    public static class AccountSessionFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly GuidField PublicId = new();
        public static readonly LongField AccountId = new() { Minimum = 1 };
        public static readonly LongField ActiveProfileId = new() { Minimum = 1, Required = false };
        public static readonly ByteArrayField TokenHash = new() { MaximumLength = 32, MinimumLength = 32 };
        public static readonly DateTimeField ExpiresAt = new();
        public static readonly DateTimeField RevokedAt = new() { Required = false };
        public static readonly DateTimeField CreatedAt = new();
        public static readonly DateTimeField LastSeenAt = new() { Required = false };
        public static readonly LongField CredentialRevision = new() { Minimum = 1 };
        public static readonly LongField RotationRevision = new() { Minimum = 1 };
        public static readonly DateTimeField RotatedAt = new() { Required = false };
        public static readonly StringField DeviceName = new(NpgsqlDbType.Varchar) { MaximumLength = 100, Required = false, AllowBlank = false, Trim = true };
        public static readonly StringField DevicePlatform = new(NpgsqlDbType.Varchar) { MaximumLength = 32, Required = false, AllowBlank = false, Trim = true };
        public static readonly GuidField DeviceId = new() { Required = false };
        public static readonly EnumField PlaybackQuality = new(typeof(Enums.Account.PlaybackQuality));
    }
}
