namespace Jularr.Backend.Data.Fields.Media
{
    public static class GamePlatformFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Name = new() { MaximumLength = 200 };
        public static readonly StringField Abbreviation = new() { MaximumLength = 32, Required = false };
        public static readonly IntField IgdbId = new() { Minimum = 1, Required = false };
    }
}
