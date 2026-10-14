namespace Jularr.Backend.Data.Fields.Account
{
    public static class DevicePairingAttemptFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly LongField AccountId = new() { Minimum = 1 };
        public static readonly DateTimeField AttemptedAt = new();
    }
}
