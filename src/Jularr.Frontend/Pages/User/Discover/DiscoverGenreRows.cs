using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Frontend.Pages.User.Discover
{
    // The popular genres each mode shows as rows of their own on Discover, as streaming sites do: the scopes (media types) whose provider answers the genre well.
    // A genre one of a mode's types does not know (Horror for series) is asked of the other types only. Each genre is one of the scope's filter genres.
    public static class DiscoverGenreRows
    {
        private static readonly IReadOnlyDictionary<string, (Genre Genre, string[] Scopes)[]> Modes = new Dictionary<string, (Genre, string[])[]>(StringComparer.Ordinal)
        {
            ["watch"] =
            [
                (Genre.Action, ["movies", "series"]), (Genre.Comedy, ["movies", "series"]), (Genre.Drama, ["movies", "series"]), (Genre.ScienceFiction, ["movies", "series"]),
                (Genre.Crime, ["movies", "series"]), (Genre.Horror, ["movies"]), (Genre.Animation, ["movies", "series"]), (Genre.Documentary, ["movies", "series"])
            ],
            ["animanga"] =
            [
                (Genre.Action, ["anime", "manga", "lightnovels"]), (Genre.Romance, ["anime", "manga", "lightnovels"]), (Genre.Comedy, ["anime", "manga", "lightnovels"]),
                (Genre.Fantasy, ["anime", "manga", "lightnovels"]), (Genre.SliceOfLife, ["anime", "manga", "lightnovels"]), (Genre.Mystery, ["anime", "manga", "lightnovels"]),
                (Genre.Horror, ["anime", "manga", "lightnovels"]), (Genre.Mecha, ["anime", "manga"])
            ],
            ["read"] =
            [
                (Genre.Fantasy, ["books"]), (Genre.Crime, ["books"]), (Genre.Mystery, ["books"]), (Genre.Romance, ["books"]), (Genre.ScienceFiction, ["books"]),
                (Genre.Thriller, ["books"]), (Genre.Horror, ["books"]), (Genre.History, ["books"])
            ],
            ["hear"] =
            [
                (Genre.Rock, ["music"]), (Genre.Pop, ["music"]), (Genre.HipHop, ["music"]), (Genre.Electronic, ["music"]), (Genre.Jazz, ["music"]), (Genre.Metal, ["music"]),
                (Genre.Classical, ["music"]), (Genre.Indie, ["music"])
            ],
            ["play"] =
            [
                (Genre.Shooter, ["games"]), (Genre.RolePlaying, ["games"]), (Genre.Adventure, ["games"]), (Genre.Strategy, ["games"]), (Genre.Indie, ["games"]),
                (Genre.Racing, ["games"]), (Genre.Sports, ["games"]), (Genre.Puzzle, ["games"])
            ]
        };

        // The genre rows of a mode, in their order; none for an unknown mode.
        public static IReadOnlyList<(Genre Genre, string[] Scopes)> For(string modeKey) => Modes.TryGetValue(modeKey, out var rows) ? rows : [];
    }
}
