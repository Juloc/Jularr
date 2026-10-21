namespace Jularr.Backend.Data.Enums.Acquisition
{
    // The language a file falls back to when none carries an asked language: the original of the work, English, or none.
    public enum LanguageFallbackType : byte
    {
        Original = 1,
        English = 2,
        None = 3
    }
}
