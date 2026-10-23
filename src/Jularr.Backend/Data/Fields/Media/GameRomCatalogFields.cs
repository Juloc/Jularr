using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class GameRomCatalogFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 100, AllowBlank = false };
        public static readonly StringField Version = new(NpgsqlDbType.Varchar) { MaximumLength = 32, Required = false };
        public static readonly DateTimeField ImportedAt = new();
    }
}
