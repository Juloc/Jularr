namespace Jularr.Backend.Data.Fields.Reading
{
    public static class ReadingBookmarkFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly IntField Block = new() { Minimum = 0, Maximum = 10_000_000 };
    }
}
