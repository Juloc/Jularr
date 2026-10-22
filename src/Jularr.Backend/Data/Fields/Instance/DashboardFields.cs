using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Instance
{
    public static class DashboardFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly LongField Count = new() { Minimum = 0 };
        public static readonly LongField Bytes = new() { Minimum = 0 };
        public static readonly LongField OptionalBytes = new() { Minimum = 0, Required = false };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 128, AllowBlank = false };
    }
}
