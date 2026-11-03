namespace Jularr.Backend.Data.Enums.Media
{
    // What Discover filters a catalog's titles by, in one set of values for every catalog.
    public enum CatalogFormat : byte
    {
        Tv = 1,
        TvShort = 2,
        Movie = 3,
        Special = 4,
        Ova = 5,
        Ona = 6,
        Music = 7,
        Manga = 8,
        Novel = 9,
        OneShot = 10
    }

    public enum ReleaseStatus : byte
    {
        Finished = 1,
        Releasing = 2,
        NotYetReleased = 3,
        Cancelled = 4,
        Hiatus = 5
    }

    // What a title was adapted from.
    public enum CatalogSource : byte
    {
        Original = 1,
        Manga = 2,
        LightNovel = 3,
        VisualNovel = 4,
        VideoGame = 5,
        Other = 6,
        Novel = 7,
        Doujinshi = 8,
        Anime = 9,
        WebNovel = 10,
        LiveAction = 11,
        Game = 12,
        Comic = 13,
        MultimediaProject = 14,
        PictureBook = 15
    }

    // AniList's and TMDB's genres together.
    public enum Genre : byte
    {
        Action = 1,
        Adventure = 2,
        Comedy = 3,
        Drama = 4,
        Ecchi = 5,
        Fantasy = 6,
        Hentai = 7,
        Horror = 8,
        MahouShoujo = 9,
        Mecha = 10,
        Music = 11,
        Mystery = 12,
        Psychological = 13,
        Romance = 14,
        ScienceFiction = 15,
        SliceOfLife = 16,
        Sports = 17,
        Supernatural = 18,
        Thriller = 19,
        Animation = 20,
        Crime = 21,
        Documentary = 22,
        Family = 23,
        History = 24,
        TvMovie = 25,
        War = 26,
        Western = 27,
        Kids = 28,
        News = 29,
        Reality = 30,
        Soap = 31,
        Talk = 32,
        Politics = 33
    }

    // The filters Discover offers a scope, as its catalog can answer them.
    public enum DiscoverFilter : byte
    {
        Genre = 1,
        Year = 2,
        Season = 3,
        Format = 4,
        Status = 5,
        Country = 6,
        Source = 7,
        Tag = 8,
        Site = 9,
        Episodes = 10,
        Chapters = 11,
        Volumes = 12,
        Duration = 13,
        Language = 14,
        Adult = 15
    }
}
