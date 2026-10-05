using System.Text.RegularExpressions;

namespace Jularr.Tests;

[TestClass]
public sealed class VideoProgressArchitectureGuardTests
{
    // Matches uses of the legacy entities and their DbSets only; ClientEpisodeProgress, EpisodeProgressService and
    // read-model members that merely share the name are other things.
    private static readonly Regex LegacyProgressTables = new(@"<(EpisodeProgress|EpisodePlaybackHistoryEntry)>|\bnew (EpisodeProgress|EpisodePlaybackHistoryEntry)\b|\b[dD]b\.(EpisodeProgress|EpisodePlaybackHistory)\b", RegexOptions.Compiled);

    // The legacy tables are only a one-time migration source: the EF mapping, the entity classes and the canonical backfill may name them.
    private static readonly string[] AllowedFiles =
    [
        "Data/AppDbContext.cs",
        "Features/Progress/EpisodeProgress.cs",
        "Features/Progress/VideoProgress.cs"
    ];

    [TestMethod]
    public void LegacyEpisodeProgressTablesHaveNoRuntimeReadersOrWriters()
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "src", "Jularr.Web");
        var offenders = Directory
            .EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".cshtml", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(sourceRoot, path).Replace('\\', '/'))
            .Where(relative => !relative.StartsWith("Data/Migrations/", StringComparison.Ordinal))
            .Where(relative => !relative.StartsWith("obj/", StringComparison.Ordinal) && !relative.StartsWith("bin/", StringComparison.Ordinal))
            .Where(relative => !AllowedFiles.Contains(relative))
            .Where(relative => LegacyProgressTables.IsMatch(File.ReadAllText(Path.Combine(sourceRoot, relative))))
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), offenders, "Canonical video state is MediaProgress/MediaPlaybackHistory; only the one-time backfill may touch the legacy tables.");
    }

    [TestMethod]
    public void ThePlayerNeverDeclaresCompletionFromAPositionAlone()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "episode-player.js"));

        StringAssert.Contains(script, "reachedCompletionNaturally(positionMs)");
        StringAssert.Contains(script, "crossedThresholdByPlayback = true");
        StringAssert.Contains(script, "naturalPositionMs < thresholdMs && positionMs >= thresholdMs", "The threshold must be crossed by continuous playback from below it.");
        StringAssert.Contains(script, "crossedThresholdByPlayback = false", "A seek clears the crossing, so a seek at or beyond the threshold completes only through ended.");
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
