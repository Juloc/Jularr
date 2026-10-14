namespace Jularr.Backend.Data.Fields.Playback
{
    public static class TrickplayFields
    {
        public static readonly IntField IntervalSeconds = new() { Minimum = 1, Maximum = 600 };
        public static readonly IntField Tiles = new() { Minimum = 1, Maximum = 100 };
        public static readonly IntField Pixels = new() { Minimum = 1, Maximum = 4096 };
        public static readonly IntField Sheet = new() { Minimum = 1, Maximum = 10_000 };
    }
}
