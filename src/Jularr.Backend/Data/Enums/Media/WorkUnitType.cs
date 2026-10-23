namespace Jularr.Backend.Data.Enums.Media
{
    public enum WorkUnitType : byte
    {
        Season = 1,
        Episode = 2,
        Volume = 3,
        Chapter = 4,
        Track = 5
    }

    public static class WorkUnitTypes
    {
        // Leaf units a Work of this media type may contain.
        public static IReadOnlyList<WorkUnitType> For(MediaType mediaType) => mediaType switch
        {
            MediaType.Series => [WorkUnitType.Episode],
            MediaType.Book => [WorkUnitType.Volume, WorkUnitType.Chapter],
            MediaType.Music => [WorkUnitType.Track],
            _ => []
        };

        // Parent grouping for a leaf unit; required for episodes, optional for chapters.
        public static WorkUnitType? ParentOf(WorkUnitType unit) => unit switch
        {
            WorkUnitType.Episode => WorkUnitType.Season,
            WorkUnitType.Chapter => WorkUnitType.Volume,
            _ => null
        };
    }
}
