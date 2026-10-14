using Jularr.Backend.Data.Enums.Instance;

namespace Jularr.Backend.Data.Fields.Instance
{
    public static class RecentLogFields
    {
        public static readonly DateTimeField At = new();
        public static readonly EnumField Level = new(typeof(RecentLogLevel));
        public static readonly StringField Category = new() { MaximumLength = 500 };
        public static readonly StringField Message = new() { MaximumLength = 4000 };
    }
}
