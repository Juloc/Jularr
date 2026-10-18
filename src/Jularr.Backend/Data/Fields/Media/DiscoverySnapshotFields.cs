namespace Jularr.Backend.Data.Fields.Media
{
    public static class DiscoverySnapshotFields
    {
        public static readonly StringField Key = new() { MaximumLength = 512, AllowBlank = false };
        public static readonly StringField ProviderKey = new() { MaximumLength = 64, AllowBlank = false };
        public static readonly StringField Payload = new() { AllowBlank = false };
        public static readonly DateTimeField FetchedAt = new();
        public static readonly DateTimeField FreshUntil = new();
        public static readonly DateTimeField RetainUntil = new();
    }
}
