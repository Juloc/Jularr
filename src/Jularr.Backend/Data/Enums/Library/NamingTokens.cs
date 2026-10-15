namespace Jularr.Backend.Data.Enums.Library
{
    // The placeholders of naming patterns; season and episode take a number format such as {Season:00}.
    public static class NamingTokens
    {
        public static IReadOnlyList<string> All { get; } = ["Title", "Year", "Season", "Episode", "Episode Title", "Quality", "Release Group", "Edition", "Proper"];

        // The placeholders that take a number format.
        public static IReadOnlyList<string> Numbered { get; } = ["Season", "Episode"];

        public static string Display(string token) => "{" + token + (Numbered.Contains(token) ? ":00" : "") + "}";
    }
}
