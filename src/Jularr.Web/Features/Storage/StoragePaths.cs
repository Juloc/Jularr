namespace Jularr.Web.Features.Storage;

/// <summary>Folder relations shared by every place that must keep one storage role out of another.</summary>
public static class StoragePaths
{
    /// <summary>Two folders are the same or one lies inside the other.</summary>
    public static bool Overlaps(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.Ordinal) ||
        IsBelow(left, right) ||
        IsBelow(right, left);

    /// <summary><paramref name="path"/> lies strictly inside <paramref name="root"/>.</summary>
    public static bool IsBelow(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
               full.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
