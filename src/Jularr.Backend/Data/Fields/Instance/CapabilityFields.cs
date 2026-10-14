namespace Jularr.Backend.Data.Fields.Instance
{
    public static class CapabilityFields
    {
        public static readonly StringArrayField Permissions = new() { ItemMaximumLength = 64 };
        public static readonly StringArrayField Modules = new() { ItemMaximumLength = 32 };
    }
}
