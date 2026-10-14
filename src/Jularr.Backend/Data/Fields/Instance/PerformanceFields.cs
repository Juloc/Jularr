namespace Jularr.Backend.Data.Fields.Instance
{
    public static class PerformanceFields
    {
        public static readonly DateTimeField At = new();
        public static readonly DecimalField ProcessorPercent = new() { Minimum = 0, Maximum = 100, Scale = 1 };
        public static readonly LongField MemoryBytes = new() { Minimum = 0 };
        public static readonly StringField Service = new() { MaximumLength = 200 };
        public static readonly LongField Count = new() { Minimum = 0 };
        public static readonly IntField Milliseconds = new() { Minimum = 0 };
    }
}
