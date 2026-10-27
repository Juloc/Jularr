using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Playback
{
    public static class CompanionFields
    {
        public static readonly GuidField Id = new();
        public static readonly StringField Token = new(NpgsqlDbType.Varchar) { MaximumLength = 64, AllowBlank = false };
        public static readonly LongField Revision = new() { Minimum = 0 };
        public static readonly LongField CommandRevision = new() { Minimum = 1 };
        public static readonly StringField Action = new(NpgsqlDbType.Varchar) { MaximumLength = 8, AllowBlank = false };
        public static readonly BoolField IsPlaying = new();
        public static readonly BoolField Succeeded = new();
        public static readonly DateTimeField ReportedAt = new() { Required = false };
    }
}
