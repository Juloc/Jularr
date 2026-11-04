using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Data.Enums.Acquisition
{
    // The media types a quality profile applies to, as the media-type bar names them: an anime film or series is Anime, a manga or light novel its own type.
    public enum AcquisitionMediaKind : byte
    {
        Movie = 1,
        Series = 2,
        Anime = 3,
        Book = 4,
        Manga = 5,
        LightNovel = 6,
        Music = 7,
        Game = 8
    }

    // The quality lists of the kinds; a profile joins only kinds of one list.
    public enum QualityFamily : byte
    {
        Video = 1,
        Book = 2,
        Manga = 3,
        Music = 4,
        Game = 5
    }

    public static class AcquisitionMediaKinds
    {
        public static AcquisitionMediaKind Of(MediaType mediaType, MediaSubtype? subtype) => subtype switch
        {
            MediaSubtype.Anime => AcquisitionMediaKind.Anime,
            MediaSubtype.Manga => AcquisitionMediaKind.Manga,
            MediaSubtype.LightNovel => AcquisitionMediaKind.LightNovel,
            _ => mediaType switch
            {
                MediaType.Movie => AcquisitionMediaKind.Movie,
                MediaType.Series => AcquisitionMediaKind.Series,
                MediaType.Book => AcquisitionMediaKind.Book,
                MediaType.Music => AcquisitionMediaKind.Music,
                _ => AcquisitionMediaKind.Game
            }
        };

        public static QualityFamily Family(AcquisitionMediaKind kind) => kind switch
        {
            AcquisitionMediaKind.Movie or AcquisitionMediaKind.Series or AcquisitionMediaKind.Anime => QualityFamily.Video,
            AcquisitionMediaKind.Book or AcquisitionMediaKind.LightNovel => QualityFamily.Book,
            AcquisitionMediaKind.Manga => QualityFamily.Manga,
            AcquisitionMediaKind.Music => QualityFamily.Music,
            _ => QualityFamily.Game
        };

        // The media types of a kind, for the reads that still filter by type (an anime is a film or a series).
        public static MediaType[] Types(AcquisitionMediaKind kind) => kind switch
        {
            AcquisitionMediaKind.Movie => [MediaType.Movie],
            AcquisitionMediaKind.Series => [MediaType.Series],
            AcquisitionMediaKind.Anime => [MediaType.Movie, MediaType.Series],
            AcquisitionMediaKind.Music => [MediaType.Music],
            AcquisitionMediaKind.Game => [MediaType.Game],
            _ => [MediaType.Book]
        };
    }
}
