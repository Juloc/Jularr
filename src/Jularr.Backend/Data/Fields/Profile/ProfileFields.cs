using NpgsqlTypes;
using Jularr.Backend.Data.Enums.Field;

namespace Jularr.Backend.Data.Fields.Profile
{
    public static class ProfileFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField DisplayName = new() { AllowBlank = false, Trim = true };
        public static readonly StringField AccentHex = new(NpgsqlDbType.Varchar) { AllowBlank = false, MinimumLength = 7, MaximumLength = 7, Trim = true };
        public static readonly LongField UiLocaleId = new() { Minimum = 1 };
        public static readonly StringField FirstContentLanguage = new(NpgsqlDbType.Varchar) { Required = false, MaximumLength = 2, Format = StringFormat.Language };
        public static readonly StringField SecondContentLanguage = new(NpgsqlDbType.Varchar) { Required = false, MaximumLength = 2, Format = StringFormat.Language };
        public static readonly LongField OwnerAccountId = new() { Minimum = 1 };
        public static readonly StringField PinHash = new() { Required = false };
        public static readonly IntField PinLength = new() { Minimum = 3, Maximum = 6, Required = false };
        public static readonly IntField PinFailedCount = new() { Minimum = 0, Maximum = 4 };
        public static readonly DateTimeField PinLockedUntil = new() { Required = false };
        public static readonly DateTimeField CreatedAt = new();
        public static readonly DateTimeField UpdatedAt = new();
    }
}
