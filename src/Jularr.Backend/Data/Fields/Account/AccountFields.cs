using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Enums.Field;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Account
{
    public static class AccountFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };

        // The name to sign in with: unique without regard to case.
        public static readonly StringField Username = new(NpgsqlDbType.Citext)
        {
            AllowBlank = false,
            MinimumLength = 3,
            MaximumLength = 32,
            Format = StringFormat.Username
        };

        // Optional, unique when set.
        public static readonly StringField Email = new(NpgsqlDbType.Citext)
        {
            AllowBlank = false,
            MaximumLength = 254,
            Format = StringFormat.Email,
            Required = false
        };

        public static readonly StringField DisplayName = new() { AllowBlank = false, Trim = true };
        public static readonly EnumField AccountRoleTypeId = new(typeof(AccountRoleType));
        public static readonly BoolField IsEnabled = new();
        public static readonly BoolField MayConfirmSources = new();
        public static readonly BoolField TranscodingPriority = new();
        public static readonly DateTimeField EmailVerifiedAt = new() { Required = false };
        public static readonly LongField CredentialRevision = new() { Minimum = 1 };

        public static readonly DateTimeField CreatedAt = new();
        public static readonly DateTimeField UpdatedAt = new();
    }

    public static class AccountRoleTypeFields
    {
        public static readonly EnumField Id = new(typeof(AccountRoleType));
        public static readonly StringField Key = new() { AllowBlank = false };
    }
}
