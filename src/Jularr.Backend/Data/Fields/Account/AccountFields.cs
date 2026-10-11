using Jularr.Data.Enums.Account;
using NpgsqlTypes;

namespace Jularr.Data.Fields.Account;

public static class AccountFields
{
    public static readonly LongField Id = new() { Minimum = 1 };

    public static readonly StringField Email = new(NpgsqlDbType.Citext) { AllowBlank = false };
    public static readonly StringField DisplayName = new() { AllowBlank = false, Trim = true };
    public static readonly EnumField AccountRoleTypeId = new(typeof(AccountRoleType));
    public static readonly BoolField IsEnabled = new();
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

public static class AccountProfileFields
{
    public static readonly LongField AccountId = new() { Minimum = 1 };
    public static readonly LongField ProfileId = new() { Minimum = 1 };
    public static readonly DateTimeField CreatedAt = new();
}
