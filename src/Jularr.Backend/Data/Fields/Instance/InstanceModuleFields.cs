using Jularr.Backend.Data.Fields;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Instance
{
    public static class InstanceModuleFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Key = new(NpgsqlDbType.Varchar) { MaximumLength = 32, AllowBlank = false };
        public static readonly BoolField IsEnabled = new();
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly StringArrayField ModuleKeys = new();
    }
}
