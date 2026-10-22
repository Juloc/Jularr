using Jularr.Backend.Data.Enums.Instance;

namespace Jularr.Backend.Data.Fields.Instance
{
    public static class LogFields
    {
        public static readonly DateTimeField At = new();
        public static readonly EnumField Level = new(typeof(LogLevelType));
        public static readonly EnumField Source = new(typeof(LogSourceType));
        public static readonly EnumField LevelFilter = new(typeof(LogLevelType)) { Required = false };
        public static readonly EnumField SourceFilter = new(typeof(LogSourceType)) { Required = false };
        public static readonly StringField Origin = new() { MaximumLength = 500 };
        public static readonly StringField Message = new() { MaximumLength = 4000 };
        public static readonly StringField Search = new() { MaximumLength = 200, Required = false, Trim = true };
        public static readonly IntField Take = new() { Minimum = 1, Maximum = 100_100 };
    }
}
