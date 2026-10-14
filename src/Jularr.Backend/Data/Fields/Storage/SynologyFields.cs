using Jularr.Backend.Data.Enums.Field;
using Jularr.Backend.Data.Enums.Storage;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Storage
{
    public static class SynologyFields
    {
        public static readonly StringField SynologyUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 2048, Required = false, Trim = true, Format = StringFormat.HttpUrl };
        public static readonly StringField SynologyCertificateSha256 = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false, Trim = true };
        public static readonly BoolField IsCertificateValid = new();
        public static readonly BoolField IsCertificateTrusted = new();
        public static readonly StringField SynologyUsername = new(NpgsqlDbType.Varchar) { MaximumLength = 256, Required = false, Format = StringFormat.SingleLine };
        public static readonly EnumField SynologyPowerTypeId = new(typeof(SynologyPowerType));
        public static readonly StringField Text = new() { MaximumLength = 256, Required = false };
        public static readonly IntField Temperature = new() { Minimum = -50, Maximum = 150, Required = false };
        public static readonly LongField Bytes = new() { Minimum = 0, Required = false };
    }
}
