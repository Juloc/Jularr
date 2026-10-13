using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class GameRomFields
    {
        public static readonly StringField Sha1 = new(NpgsqlDbType.Varchar) { MaximumLength = 40, AllowBlank = false };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 500, AllowBlank = false };
        public static readonly StringField Payload = new() { AllowBlank = false };
    }
}
