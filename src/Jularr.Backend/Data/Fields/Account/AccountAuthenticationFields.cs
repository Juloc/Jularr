using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Account
{
    public static class AccountPasswordFields
    {
        public static readonly LongField AccountId = new() { Minimum = 1 };
        public static readonly StringField PasswordHash = new() { AllowBlank = false };
        public static readonly DateTimeField ChangedAt = new();
    }

    public static class AccountPasskeyFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly LongField AccountId = new() { Minimum = 1 };

        public static readonly ByteArrayField CredentialId = new()
        {
            MaximumLength = 1024,
            MinimumLength = 1
        };
        public static readonly ByteArrayField PublicKey = new()
        {
            MaximumLength = 16384,
            MinimumLength = 1
        };
        public static readonly LongField SignatureCounter = new() { Minimum = 0 };
        public static readonly GuidField Aaguid = new() { Required = false, AllowEmpty = true };
        public static readonly StringArrayField Transports = new();
        public static readonly BoolField BackupEligible = new() { Required = false };
        public static readonly BoolField BackedUp = new() { Required = false };
        public static readonly BoolField UserVerificationRequired = new() { Required = false };
        public static readonly StringField DisplayName = new(NpgsqlDbType.Varchar)
        {
            MaximumLength = 100,
            AllowBlank = false
        };

        public static readonly DateTimeField CreatedAt = new();
        public static readonly DateTimeField LastUsedAt = new() { Required = false };
        public static readonly DateTimeField RevokedAt = new() { Required = false };
    }

    public static class AccountTotpFactorFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly LongField AccountId = new() { Minimum = 1 };
        public static readonly ByteArrayField EncryptedSecret = new()
        {
            MaximumLength = 16384,
            MinimumLength = 1
        };
        public static readonly DateTimeField CreatedAt = new();
        public static readonly DateTimeField ConfirmedAt = new() { Required = false };
        public static readonly LongField LastAcceptedStep = new() { Minimum = -1 };
        public static readonly DateTimeField RevokedAt = new() { Required = false };
    }

    public static class AccountRecoveryCodeFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly LongField AccountId = new() { Minimum = 1 };
        public static readonly ByteArrayField CodeHash = new()
        {
            MaximumLength = 32,
            MinimumLength = 32
        };
        public static readonly DateTimeField CreatedAt = new();
        public static readonly DateTimeField UsedAt = new() { Required = false };
    }

    public static class AccountExternalLoginFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly LongField AccountId = new() { Minimum = 1 };
        public static readonly LongField ProviderId = new() { Minimum = 1 };
        public static readonly StringField ExternalAccountId = new()
        {
            AllowBlank = false,
            MaximumLength = 200
        };
        public static readonly DateTimeField CreatedAt = new();
    }
}
