namespace Jularr.Backend.Data.Fields.Account
{
    public static class AccountProfileFields
    {
        public static readonly LongField AccountId = new() { Minimum = 1 };
        public static readonly LongField ProfileId = new() { Minimum = 1 };
        public static readonly DateTimeField CreatedAt = new();
    }
}
