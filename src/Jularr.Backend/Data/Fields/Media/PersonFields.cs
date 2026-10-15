using Jularr.Backend.Data.Enums.Media;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    // A person, band or studio and what they did for a Work: the character played or the provider's name for the job.
    public static class PersonFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 300, AllowBlank = false };
        public static readonly StringField ImageUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false };
        public static readonly EnumField PersonRoleId = new(typeof(PersonRole));
        public static readonly StringField Credit = new(NpgsqlDbType.Varchar) { MaximumLength = 300, Required = false };
    }
}
