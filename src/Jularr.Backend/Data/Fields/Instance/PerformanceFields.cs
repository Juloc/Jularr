namespace Jularr.Backend.Data.Fields.Instance
{
    public static class PerformanceFields
    {
        public static readonly DateTimeField At = new();
        public static readonly DecimalField ProcessorPercent = new() { Minimum = 0, Maximum = 100, Scale = 1 };
        public static readonly LongField MemoryBytes = new() { Minimum = 0 };
        public static readonly StringField Service = new() { MaximumLength = 200, AllowBlank = false };
        public static readonly LongField Count = new() { Minimum = 0 };
        public static readonly IntField Milliseconds = new() { Minimum = 0 };

        // What is kept of the history: the hour a slice began, and its figures.
        public static readonly DateTimeField SliceStartedAt = new();
        public static readonly LongField TotalMilliseconds = new() { Minimum = 0 };

        // A batch of samples and slices as parallel lists, one entry per row; times are seconds since the Unix epoch.
        public static readonly LongArrayField Seconds = new() { Minimum = 0 };
        public static readonly LongArrayField Numbers = new() { Minimum = 0 };
        public static readonly StringArrayField Services = new() { ItemMaximumLength = 200 };
    }
}
