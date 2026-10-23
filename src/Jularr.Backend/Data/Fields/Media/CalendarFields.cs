namespace Jularr.Backend.Data.Fields.Media
{
    public static class CalendarFields
    {
        public static readonly DateField Day = new();
        public static readonly IntField Days = new() { Minimum = 1, Maximum = 62 };
        public static readonly BoolField IsAvailable = new();
        public static readonly BoolField IsWatchlisted = new();
        public static readonly BoolField IsRequested = new();
    }
}
