namespace Jularr.Tests;

/// <summary>
/// The markup and scripts of the web video player of the Anime episode page. The player stage and its script tags live in
/// shared partials (the same ones the Movie and Series Watch page renders), so the source guards that read "the episode
/// page" read the page together with them, in the order the browser receives them.
/// </summary>
internal static class EpisodePlayerSource
{
    public static string Read(string repositoryRoot)
    {
        var pages = Path.Combine(repositoryRoot, "src", "Jularr.Web", "Pages", "Library");
        return string.Join(
            "\n",
            File.ReadAllText(Path.Combine(pages, "Episode.cshtml")),
            File.ReadAllText(Path.Combine(pages, "_VideoPlayerStage.cshtml")),
            File.ReadAllText(Path.Combine(pages, "_VideoPlayerScripts.cshtml")));
    }
}
