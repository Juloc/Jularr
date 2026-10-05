using System.Text.RegularExpressions;

namespace Jularr.Web.Features.Playback.Transcoding;

/// <summary>
/// The one rule for which directories under the HLS cache root Jularr may delete: session directories
/// (32 hex characters, never a symbolic link) inside a root Jularr owns. Every deleter of HLS cache
/// content (the session manager's sweeper, Admin Storage cleanup and its scan) asks this class, because
/// the root is an Admin-chosen path and may sit next to foreign files.
/// </summary>
public static partial class PlaybackCacheOwnership
{
    public const string MarkerFileName = ".jularr-hls-cache";

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionDirectoryName();

    public static bool IsSessionDirectoryName(string name) => SessionDirectoryName().IsMatch(name);

    /// <summary>
    /// Whether the root is Jularr's: marked by an earlier session, the default folder, or holding
    /// nothing at all (a missing folder counts as empty). Anything else is somebody else's folder.
    /// </summary>
    public static bool IsOwnedRoot(string root) =>
        !Directory.Exists(root) ||
        File.Exists(Path.Combine(root, MarkerFileName)) ||
        string.Equals(root.TrimEnd('/', '\\'),PlaybackTranscodingSettings.DefaultHlsCachePath, StringComparison.Ordinal) ||
        !Directory.EnumerateFileSystemEntries(root).Any();

    public static void MarkRoot(string root)
    {
        var marker = Path.Combine(root, MarkerFileName);
        if (!File.Exists(marker))
        {
            File.WriteAllBytes(marker, []);
        }
    }

    /// <summary>A session directory of an owned root, safe to delete recursively: symbolic links are never followed.</summary>
    public static bool IsDeletableSession(string root, string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var parent = info.Parent?.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var expected = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return IsSessionDirectoryName(info.Name) &&
                   info.Exists &&
                   (info.Attributes & FileAttributes.ReparsePoint) == 0 &&
                   string.Equals(parent, expected, comparison) &&
                   IsOwnedRoot(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
