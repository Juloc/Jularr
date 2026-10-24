namespace Jularr.Backend.Data.Enums.Acquisition
{
    // Book fallback policy after the requested languages.
    public enum LanguageFallbackType : byte
    {
        Original = 1,
        English = 2,
        None = 3,
        Any = 4
    }
}
