namespace Jularr.Tests;

[TestClass]
public sealed class VideoProgressArchitectureGuardTests
{
    private static readonly string[] LegacyProgressMembers = ["db.EpisodeProgress", "db.EpisodePlaybackHistory", "EpisodeProgress>", "EpisodePlaybackHistoryEntry>"];

    // The legacy tables are only a one-time migration source: the EF mapping and the canonical backfill may name them.
    private static readonly string[] AllowedFiles = ["AppDbContext.cs", "VideoProgress.cs", "EpisodeProgress.cs"];

    [TestMethod]
    public void LegacyEpisodeProgressTablesHaveNoRuntimeReadersOrWriters()
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "src", "Jularr.Web");
        var offenders = Directory
            .EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".cshtml", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !AllowedFiles.Contains(Path.GetFileName(path)))
            .Where(path => LegacyProgressMembers.Any(member => File.ReadAllText(path).Contains(member, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), offenders, "Canonical video state is MediaProgress/MediaPlaybackHistory; only the one-time backfill may touch the legacy tables.");
    }

    [TestMethod]
    public void ThePlayerNeverDeclaresCompletionFromAPositionAlone()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "episode-player.js"));

        StringAssert.Contains(script, "reachedCompletionNaturally(positionMs)");
        StringAssert.Contains(script, "naturalPositionMs");
        Assert.IsFalse(script.Contains("completed: positionMs", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }
}
