using Jularr.Backend.Data.Enums.Field;

namespace Jularr.Backend.Data.Fields.Network
{
    public static class VpnProfileFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Name = new() { MaximumLength = 100, AllowBlank = false, Trim = true, Format = StringFormat.SingleLine };
        public static readonly FileField File = new() { MaximumBytes = 16384, Extensions = [".conf"] };
        public static readonly StringField Configuration = new() { MaximumLength = 16384, Required = false };
        public static readonly ByteArrayField ProtectedConfiguration = new();
        public static readonly StringField EndpointHost = new() { MaximumLength = 253, AllowBlank = false };
        public static readonly IntField EndpointPort = new() { Minimum = 1, Maximum = 65535 };
        public static readonly StringArrayField Addresses = new() { MaximumLength = 8, ItemMaximumLength = 64 };
        public static readonly BoolField HasIpv6Route = new();
        public static readonly LongField Version = new() { Minimum = 1 };
    }
}
