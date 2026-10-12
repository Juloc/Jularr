using Jularr.Backend.Data.Fields;

namespace Jularr.Backend.Data.Fields.Provider
{
    public static class ProviderFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Key = new();
        public static readonly BoolField IsEnabled = new();
        public static readonly ByteArrayField ProtectedSecret = new() { Required = false };
    }

    // Write-only credential input and its presence.
    public static class ProviderCredentialFields
    {
        public static readonly StringField Secret = new() { MaximumLength = 1024, Required = false, Trim = true, Format = Enums.Field.StringFormat.SingleLine };
        public static readonly BoolField HasSecret = new();
    }
}
