namespace Jularr.Web.Features.Acquisition.Import;

/// <summary>A sidecar of a placed file and the name it gets next to the placed file.</summary>
public sealed record PlacedSidecar(
    string SourcePath,
    string DestinationName);

/// <summary>
/// One file to place in the library: where it comes from and goes, how, which sidecars follow it
/// and which existing files it replaces once it is in place (an upgrade).
/// </summary>
public sealed record LibraryFilePlacement(
    string SourcePath,
    string DestinationPath,
    ImportFileAction Action,
    bool AllowHardlinkFallbackToCopy,
    IReadOnlyList<PlacedSidecar> Sidecars,
    IReadOnlyList<string> ReplaceAfterCommit);

/// <summary>
/// The one commit step of a media file into a library folder, shared by every importer that places
/// single files (Anime today): check the source and the destination, transfer the file with the
/// owner's import mode (<see cref="ImportFileTransfer"/>), move the sidecars next to it and only
/// then delete the files it replaces, so an existing file is preserved until the replacement is in
/// place. Deciding what a file is and where it belongs stays with the importer.
/// </summary>
public sealed class LibraryFilePlacer(ImportFileTransfer transfer)
{
    /// <summary>Why the source cannot be placed, or null when it exists.</summary>
    public static string? FindSourceProblem(string sourcePath) =>
        File.Exists(sourcePath) ? null : "The downloaded file no longer exists.";

    /// <summary>
    /// Why the destination cannot be used, or null. An existing destination is only acceptable
    /// when it is one of the files the placement replaces.
    /// </summary>
    public static string? FindDestinationConflict(
        string destinationPath,
        IReadOnlyCollection<string> replaceAfterCommit) =>
        File.Exists(destinationPath) &&
        !replaceAfterCommit.Any(path => SamePath(path, destinationPath))
            ? $"Destination already exists: {Path.GetFileName(destinationPath)}."
            : null;

    /// <summary>
    /// Whether an existing destination is the complete placement of the source: the sizes match, or the source is gone because a move
    /// already finished. A crash mid-copy leaves a smaller destination that must never be taken for the placed file.
    /// </summary>
    public static bool IsCompletePlacement(string sourcePath, string destinationPath) =>
        !File.Exists(sourcePath) || new FileInfo(sourcePath).Length == new FileInfo(destinationPath).Length;

    /// <summary>
    /// Places the file. Returns the notes of the steps that did not block the import (a sidecar
    /// that stayed in the download folder, a replaced file that could not be deleted).
    /// </summary>
    /// <exception cref="IOException">
    /// The library folder could not be created or the file could not be transferred; nothing was
    /// replaced. A hardlink across filesystems without the copy fallback throws
    /// <see cref="CrossDeviceLinkException"/>.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The library folder is not writable.</exception>
    public IReadOnlyList<string> Place(LibraryFilePlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        var directory = Path.GetDirectoryName(placement.DestinationPath)
            ?? throw new ArgumentException("The destination needs a folder.", nameof(placement));
        Directory.CreateDirectory(directory);
        transfer.Transfer(
            placement.SourcePath,
            placement.DestinationPath,
            placement.Action,
            placement.AllowHardlinkFallbackToCopy);

        var notes = new List<string>();
        foreach (var sidecar in placement.Sidecars)
        {
            var sidecarDestination = Path.Combine(directory, sidecar.DestinationName);
            try
            {
                if (File.Exists(sidecar.SourcePath) && !File.Exists(sidecarDestination))
                {
                    File.Move(sidecar.SourcePath, sidecarDestination);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                notes.Add($"Sidecar {Path.GetFileName(sidecar.SourcePath)} stayed in the download folder: {exception.Message}");
            }
        }

        foreach (var replaced in placement.ReplaceAfterCommit)
        {
            if (SamePath(replaced, placement.DestinationPath))
            {
                continue;
            }

            try
            {
                File.Delete(replaced);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                notes.Add($"Replaced file {Path.GetFileName(replaced)} could not be deleted: {exception.Message}");
            }
        }

        return notes;
    }

    /// <summary>Two paths name the same file: same full path, ignoring case and a trailing separator.</summary>
    public static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        static string Normalize(string path) =>
            Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

/// <summary>The destination of a placement already exists but is not a complete copy of the source (an interrupted copy, or another file of the same name).</summary>
public sealed class DestinationMismatchException(string destinationPath)
    : InvalidOperationException($"The library already has a different file at '{Path.GetFileName(destinationPath)}'. Remove it or import the release by hand.");
