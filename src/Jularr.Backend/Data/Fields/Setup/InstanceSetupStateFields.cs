namespace Jularr.Backend.Data.Fields.Setup
{
    public static class InstanceSetupStateFields
    {
        public static readonly LongField Id = new() { Minimum = 1, Maximum = 1 };
        public static readonly DateTimeField OwnerCreatedAt = new() { Required = false };
    }
}
