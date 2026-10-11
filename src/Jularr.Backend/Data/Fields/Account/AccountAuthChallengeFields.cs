using Jularr.Data.Enums.Account;
using NpgsqlTypes;

namespace Jularr.Data.Fields.Account;

public static class AuthChallengePurposeTypeFields
{
    public static readonly EnumField Id = new(typeof(AuthChallengePurposeType));
    public static readonly StringField Key = new() { AllowBlank = false };
}

public static class AccountAuthChallengeFields
{
    public static readonly LongField Id = new() { Minimum = 1 };
    public static readonly GuidField PublicId = new();
    public static readonly LongField AccountId = new() { Minimum = 1, Required = false };
    public static readonly EnumField AuthChallengePurposeTypeId = new(typeof(AuthChallengePurposeType));
    public static readonly ByteArrayField TokenHash = new()
    {
        MaximumLength = 32,
        MinimumLength = 32
    };
    public static readonly LongField CredentialRevision = new() { Minimum = 1, Required = false };
    public static readonly ByteArrayField ProtectedState = new()
    {
        MaximumLength = 16384,
        MinimumLength = 1,
        Required = false
    };
    public static readonly DateTimeField ExpiresAt = new();
    public static readonly DateTimeField ConsumedAt = new() { Required = false };
    public static readonly DateTimeField CreatedAt = new();
}

public static class AccountExternalAuthFlowFields
{
    public static readonly LongField AccountAuthChallengeId = new() { Minimum = 1 };
    public static readonly EnumField AuthChallengePurposeTypeId = new(typeof(AuthChallengePurposeType));
    public static readonly LongField ProviderId = new() { Minimum = 1 };
    public static readonly LongField StartedProfileId = new() { Minimum = 1, Required = false };
    public static readonly LongField ExternalPinId = new() { Minimum = 1 };
    public static readonly StringField ClientIdentifier = new(NpgsqlDbType.Varchar)
    {
        MaximumLength = 200,
        AllowBlank = false
    };
    public static readonly StringField VerifiedExternalAccountId = new(NpgsqlDbType.Varchar)
    {
        MaximumLength = 200,
        Required = false,
        AllowBlank = false
    };
    public static readonly StringField ReturnPath = new(NpgsqlDbType.Varchar)
    {
        MaximumLength = 2048,
        AllowBlank = false
    };
}
