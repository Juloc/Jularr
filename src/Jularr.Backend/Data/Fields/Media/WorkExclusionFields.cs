using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class WorkExclusionFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Title = new(NpgsqlDbType.Varchar) { AllowBlank = false, Trim = true, MaximumLength = 500 };
        public static readonly DateTimeField CreatedAt = new();
        public static readonly BoolField DeletesFiles = new();
        public static readonly BoolField AddsExclusion = new();
        public static readonly BoolField HasActiveDownload = new();
        public static readonly StringField Namespace = new(NpgsqlDbType.Varchar) { AllowBlank = false, MaximumLength = 16 };
        public static readonly StringField Value = new(NpgsqlDbType.Varchar) { AllowBlank = false, MaximumLength = 64 };
    }
}
