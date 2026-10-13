namespace Jularr.Backend.Data.Enums.Media
{
    // Subcategories of a parent type; a Work keeps its type and identity.
    public enum MediaSubtype : byte
    {
        Anime = 1,
        Manga = 2,
        LightNovel = 3
    }

    public static class MediaSubtypes
    {
        // Parent types a subtype belongs to, as in MediaSubtypeParents.
        public static bool Fits(MediaType mediaType, MediaSubtype? subtype) => subtype switch
        {
            null => true,
            MediaSubtype.Anime => mediaType is MediaType.Movie or MediaType.Series,
            _ => mediaType == MediaType.Book
        };
    }

    // A media category is a type or a subtype, named by its catalog key.
    public static class MediaCategories
    {
        private static readonly HashSet<string> Keys = [.. Enum.GetNames<MediaType>(), .. Enum.GetNames<MediaSubtype>()];

        public static bool IsKey(string key) => Keys.Contains(key);
    }
}
