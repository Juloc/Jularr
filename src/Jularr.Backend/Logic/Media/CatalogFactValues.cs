using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Logic.Media
{
    // The catalogs' own filter values and Jularr's, both ways.
    internal static class CatalogFactValues
    {
        public static readonly IReadOnlyDictionary<string, CatalogFormat> AniListFormats = new Dictionary<string, CatalogFormat>(StringComparer.Ordinal)
        {
            ["TV"] = CatalogFormat.Tv,
            ["TV_SHORT"] = CatalogFormat.TvShort,
            ["MOVIE"] = CatalogFormat.Movie,
            ["SPECIAL"] = CatalogFormat.Special,
            ["OVA"] = CatalogFormat.Ova,
            ["ONA"] = CatalogFormat.Ona,
            ["MUSIC"] = CatalogFormat.Music,
            ["MANGA"] = CatalogFormat.Manga,
            ["NOVEL"] = CatalogFormat.Novel,
            ["ONE_SHOT"] = CatalogFormat.OneShot
        };

        public static readonly IReadOnlyDictionary<string, ReleaseStatus> AniListStatuses = new Dictionary<string, ReleaseStatus>(StringComparer.Ordinal)
        {
            ["FINISHED"] = ReleaseStatus.Finished,
            ["RELEASING"] = ReleaseStatus.Releasing,
            ["NOT_YET_RELEASED"] = ReleaseStatus.NotYetReleased,
            ["CANCELLED"] = ReleaseStatus.Cancelled,
            ["HIATUS"] = ReleaseStatus.Hiatus
        };

        public static readonly IReadOnlyDictionary<string, CatalogSource> AniListSources = new Dictionary<string, CatalogSource>(StringComparer.Ordinal)
        {
            ["ORIGINAL"] = CatalogSource.Original,
            ["MANGA"] = CatalogSource.Manga,
            ["LIGHT_NOVEL"] = CatalogSource.LightNovel,
            ["VISUAL_NOVEL"] = CatalogSource.VisualNovel,
            ["VIDEO_GAME"] = CatalogSource.VideoGame,
            ["OTHER"] = CatalogSource.Other,
            ["NOVEL"] = CatalogSource.Novel,
            ["DOUJINSHI"] = CatalogSource.Doujinshi,
            ["ANIME"] = CatalogSource.Anime,
            ["WEB_NOVEL"] = CatalogSource.WebNovel,
            ["LIVE_ACTION"] = CatalogSource.LiveAction,
            ["GAME"] = CatalogSource.Game,
            ["COMIC"] = CatalogSource.Comic,
            ["MULTIMEDIA_PROJECT"] = CatalogSource.MultimediaProject,
            ["PICTURE_BOOK"] = CatalogSource.PictureBook
        };

        public static readonly IReadOnlyDictionary<string, Genre> AniListGenres = new Dictionary<string, Genre>(StringComparer.Ordinal)
        {
            ["Action"] = Genre.Action,
            ["Adventure"] = Genre.Adventure,
            ["Comedy"] = Genre.Comedy,
            ["Drama"] = Genre.Drama,
            ["Ecchi"] = Genre.Ecchi,
            ["Fantasy"] = Genre.Fantasy,
            ["Hentai"] = Genre.Hentai,
            ["Horror"] = Genre.Horror,
            ["Mahou Shoujo"] = Genre.MahouShoujo,
            ["Mecha"] = Genre.Mecha,
            ["Music"] = Genre.Music,
            ["Mystery"] = Genre.Mystery,
            ["Psychological"] = Genre.Psychological,
            ["Romance"] = Genre.Romance,
            ["Sci-Fi"] = Genre.ScienceFiction,
            ["Slice of Life"] = Genre.SliceOfLife,
            ["Sports"] = Genre.Sports,
            ["Supernatural"] = Genre.Supernatural,
            ["Thriller"] = Genre.Thriller
        };

        // TMDB's series genres pair two of ours.
        public static readonly IReadOnlyDictionary<int, Genre[]> TmdbGenres = new Dictionary<int, Genre[]>
        {
            [28] = [Genre.Action],
            [12] = [Genre.Adventure],
            [16] = [Genre.Animation],
            [35] = [Genre.Comedy],
            [80] = [Genre.Crime],
            [99] = [Genre.Documentary],
            [18] = [Genre.Drama],
            [10751] = [Genre.Family],
            [14] = [Genre.Fantasy],
            [36] = [Genre.History],
            [27] = [Genre.Horror],
            [10402] = [Genre.Music],
            [9648] = [Genre.Mystery],
            [10749] = [Genre.Romance],
            [878] = [Genre.ScienceFiction],
            [10770] = [Genre.TvMovie],
            [53] = [Genre.Thriller],
            [10752] = [Genre.War],
            [37] = [Genre.Western],
            [10759] = [Genre.Action, Genre.Adventure],
            [10762] = [Genre.Kids],
            [10763] = [Genre.News],
            [10764] = [Genre.Reality],
            [10765] = [Genre.ScienceFiction, Genre.Fantasy],
            [10766] = [Genre.Soap],
            [10767] = [Genre.Talk],
            [10768] = [Genre.War, Genre.Politics]
        };

        public static T? Value<T>(IReadOnlyDictionary<string, T> values, string? key) where T : struct, Enum => key is not null && values.TryGetValue(key, out var value) ? value : null;

        public static Genre[] Genres(IEnumerable<string> names) => [.. names.Select(name => Value(AniListGenres, name)).OfType<Genre>().Distinct().Order()];

        public static Genre[] Genres(IEnumerable<int> tmdbIds) => [.. tmdbIds.SelectMany(id => TmdbGenres.TryGetValue(id, out var genres) ? genres : []).Distinct().Order()];

        // TMDB names how far a film or series is in words of its own.
        public static ReleaseStatus? TmdbStatus(string? status) => status switch
        {
            "Released" or "Ended" => ReleaseStatus.Finished,
            "Returning Series" => ReleaseStatus.Releasing,
            "Canceled" => ReleaseStatus.Cancelled,
            "In Production" or "Post Production" or "Planned" or "Rumored" or "Pilot" => ReleaseStatus.NotYetReleased,
            _ => null
        };
    }
}
