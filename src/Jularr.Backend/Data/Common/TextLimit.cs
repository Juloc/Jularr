namespace Jularr.Backend.Data.Common
{
    // Provider and file text cut to the length its field allows.
    internal static class TextLimit
    {
        public static string? Cut(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
    }
}
