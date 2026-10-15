namespace Jularr.Backend.Data.Enums.Library
{
    // The placeholders of naming patterns; season and episode take a number format such as {Season:00}.
    public static class NamingTokens
    {
        public static IReadOnlyList<string> All { get; } = ["Title", "Year", "Season", "Episode", "Episode Title", "Quality", "Release Group", "Edition", "Proper"];
    }
}
