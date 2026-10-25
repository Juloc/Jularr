namespace Jularr.Backend.Data.Fields.Media
{
    public static class MediaImportJobFields
    {
        public static readonly GuidField JobId = new();
        public static readonly GuidField LatestJobId = new() { Required = false };
        public static readonly LongField Sequence = new() { Minimum = 0 };
        public static readonly StringField Stage = new() { MaximumLength = 16, AllowBlank = false };
        public static readonly LongField WorkId = new() { Minimum = 1, Required = false };
        public static readonly StringField Source = new() { MaximumLength = 48, Required = false };
        public static readonly StringField Query = new() { MaximumLength = 500, Required = false };
        public static readonly StringField Detail = new() { MaximumLength = 64, Required = false };
        public static readonly IntField CandidateCount = new() { Minimum = 0, Required = false };
        public static readonly IntField Attempt = new() { Minimum = 0 };
        public static readonly StringField HistoryJson = new() { MaximumLength = 60000 };
    }
}
