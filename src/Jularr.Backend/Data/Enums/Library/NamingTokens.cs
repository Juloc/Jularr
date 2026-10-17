namespace Jularr.Backend.Data.Enums.Library
{
    // The placeholders of naming patterns; season and episode take a number format such as {Season:00}.
    public static class NamingTokens
    {
        public static IReadOnlyList<string> All { get; } = ["Title", "Year", "Season", "Episode", "Episode Title", "Quality", "Release Group", "Edition", "Proper"];

        // Valid aliases from Sonarr/Radarr. Legacy editor-help entries remain localized.
        public static IReadOnlyList<string> Supported { get; } = [
            .. All,
            "Series Title", "Series CleanTitle", "Series TitleYear", "Series CleanTitleYear",
            "Series TitleWithoutYear", "Series CleanTitleWithoutYear", "Series TitleThe", "Series CleanTitleThe",
            "Series TitleTheYear", "Series CleanTitleTheYear", "Series TitleTheWithoutYear", "Series CleanTitleTheWithoutYear",
            "Series TitleFirstCharacter", "Series Year",
            "Movie Title", "Movie CleanTitle", "Movie TitleThe", "Movie CleanTitleThe",
            "Movie OriginalTitle", "Movie CleanOriginalTitle", "Movie TitleFirstCharacter",
            "Movie Collection", "Movie CollectionThe", "Movie CleanCollectionThe", "Movie Certification",
            "Release Year", "ImdbId", "TmdbId", "TvdbId", "TvMazeId", "AnilistId", "MusicBrainzId",
            "OpenLibraryId", "IgdbId", "GutenbergId", "IsbnId", "PlexId", "ProviderId",
            "Quality Full", "Quality Title", "Edition Tags", "Original Title", "Original Filename",
            "Custom Formats", "Custom Format", "MediaInfo Simple", "MediaInfo Full",
            "MediaInfo AudioCodec", "MediaInfo AudioChannels", "MediaInfo AudioLanguages",
            "MediaInfo AudioLanguagesAll", "MediaInfo SubtitleLanguages", "MediaInfo VideoCodec"
        ];

        // The placeholders that take a number format.
        public static IReadOnlyList<string> Numbered { get; } = ["Season", "Episode"];

        public static string Display(string token) => "{" + token + (Numbered.Contains(token) ? ":00" : "") + "}";
    }
}
