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

    // Entries a filesystem or operating system puts into a fresh volume by itself; a dedicated mount point is still "empty" with them.
    private static readonly string[] s_systemEntries = ["lost+found", "$RECYCLE.BIN", "System Volume Information"];

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionDirectoryName();

    public static bool IsSessionDirectoryName(string name) => SessionDirectoryName().IsMatch(name);

    /// <summary>
    /// Whether the root is Jularr's: marked by an earlier save or session, the default folder, or holding nothing
    /// but entries the operating system created (<c>lost+found</c>, <c>.Trash-*</c>, the Windows recycle bin). A
    /// missing folder counts as empty. Anything else is somebody else's folder.
    /// </summary>
    public static bool IsOwnedRoot(string root) =>
        !Directory.Exists(root) ||
        File.Exists(Path.Combine(root, MarkerFileName)) ||
        string.Equals(root.TrimEnd('/', '\\'), PlaybackTranscodingSettings.DefaultHlsCachePath, StringComparison.Ordinal) ||
        Directory.EnumerateFileSystemEntries(root).All(IsSystemEntry);

    /// <summary>Creates the folder and marks it Jularr's; throws the filesystem's own exception when it cannot be written.</summary>
    public static void MarkRoot(string root)
    {
        Directory.CreateDirectory(root);
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

    private static bool IsSystemEntry(string path)
    {
        var name = Path.GetFileName(path);
        return s_systemEntries.Contains(name, StringComparer.Ordinal) || name.StartsWith(".Trash-", StringComparison.Ordinal);
    }
}
