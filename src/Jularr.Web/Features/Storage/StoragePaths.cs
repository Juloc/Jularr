namespace Jularr.Web.Features.Storage;

/// <summary>Folder relations shared by every place that must keep one storage role out of another.</summary>
public static class StoragePaths
{
    // Windows paths ignore case; Unix paths do not.
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Two folders are the same or one lies inside the other.</summary>
    public static bool Overlaps(string left, string right) => AreSame(left, right) || IsBelow(left, right) || IsBelow(right, left);

    public static bool AreSame(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), Comparison);

    /// <summary><paramref name="path"/> lies strictly inside <paramref name="root"/>.</summary>
    public static bool IsBelow(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.StartsWith(parent + Path.DirectorySeparatorChar, Comparison) ||
               full.StartsWith(parent + Path.AltDirectorySeparatorChar, Comparison);
    }
}
