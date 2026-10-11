
namespace Jularr.Backend.Data.Fields.Profile
{
    public static class ProfileFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField DisplayName = new() { AllowBlank = false, Trim = true };
        public static readonly LongField UiLocaleId = new() { Minimum = 1 };
        public static readonly LongField OwnerAccountId = new() { Minimum = 1 };
        public static readonly StringField PinHash = new() { Required = false };
        public static readonly DateTimeField CreatedAt = new();
        public static readonly DateTimeField UpdatedAt = new();
    }
}
