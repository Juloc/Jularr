namespace Jularr.Backend.Data.Fields.Media
{
    public static class MediaImportJobFields
    {
        public static readonly GuidField JobId = new();
        public static readonly LongField Sequence = new() { Minimum = 0 };
        public static readonly LongField WorkId = new() { Minimum = 1, Required = false };
    }
}
