namespace Jularr.Frontend.Framework
{
    // The media types of a personal list or of Discover grouped by what a person does with them, in the order of the media-type bar.
    public static class MediaAreas
    {
        public static IReadOnlyList<MediaArea> All { get; } =
        [
            new("discover.area.watch", "play", ["movies", "series", "anime"]),
            new("discover.area.read", "book", ["books", "manga", "lightnovels"]),
            new("discover.area.hear", "music", ["music"]),
            new("discover.area.play", "gamepad", ["games"])
        ];
    }

    public sealed record MediaArea(string LabelKey, string Icon, IReadOnlyList<string> Slugs);
}
