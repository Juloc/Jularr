using Jularr.Backend.Data.Enums.Field;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Library
{
    public static class RecycledFileFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly LongArrayField Ids = new() { Minimum = 1, MaximumLength = 1000 };
        public static readonly StringField FolderPath = new(NpgsqlDbType.Varchar) { MaximumLength = 2048, Format = StringFormat.RelativePath };
    }
}
