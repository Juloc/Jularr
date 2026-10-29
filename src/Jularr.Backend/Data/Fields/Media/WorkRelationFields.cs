using Jularr.Backend.Data.Enums.Media;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class WorkRelationFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly EnumField WorkRelationTypeId = new(typeof(WorkRelationType));
        public static readonly StringField SourceKey = new(NpgsqlDbType.Varchar) { MaximumLength = 16, AllowBlank = false };

        // JSON rows of the related Works a provider names.
        public static readonly StringField Targets = new() { AllowBlank = false };
        public static readonly LongArrayField WorkIds = new() { Minimum = 1, MaximumLength = 500, IsSet = true };
    }
}
