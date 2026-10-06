using Jularr.Web.Features.Library;

namespace Jularr.Web.Features.Acquisition.Import;

/// <summary>What happens to the source file of an import: moved, copied or hardlinked.</summary>
public enum ImportFileAction
{
    Move,
    Copy,
    Hardlink
}

/// <summary>
/// The one implementation of the owner's import mode (Move / Copy / Hardlink / Hardlink or copy)
/// for a single file, shared by every media importer. It never overwrites an existing file;
/// conflict policy (skip, rename, manual review) stays with the caller.
/// </summary>
public sealed class ImportFileTransfer(IHardLinkCreator hardLinks)
{
    /// <summary>The import mode that executes a LibraryRoot placement policy; the two enums order their members differently.</summary>
    public static ImportMode ModeFor(LibraryPlacementPolicy policy) =>
        policy switch
        {
            LibraryPlacementPolicy.HardlinkOrCopy => ImportMode.HardlinkOrCopy,
            LibraryPlacementPolicy.Hardlink => ImportMode.Hardlink,
            LibraryPlacementPolicy.Copy => ImportMode.Copy,
            LibraryPlacementPolicy.Move => ImportMode.Move,
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        };

    /// <summary>The file action and cross-filesystem fallback an import mode stands for.</summary>
    public static (ImportFileAction Action, bool AllowHardlinkFallbackToCopy) Resolve(ImportMode mode) =>
        mode switch
        {
            ImportMode.Copy => (ImportFileAction.Copy, false),
            ImportMode.Hardlink => (ImportFileAction.Hardlink, false),
            ImportMode.HardlinkOrCopy => (ImportFileAction.Hardlink, true),
            _ => (ImportFileAction.Move, false)
        };

    public void Transfer(string sourcePath, string destinationPath, ImportMode mode)
    {
        var (action, allowFallback) = Resolve(mode);
        Transfer(sourcePath, destinationPath, action, allowFallback);
    }

    /// <exception cref="CrossDeviceLinkException">A hardlink crosses filesystems and no copy fallback is allowed.</exception>
    public void Transfer(
        string sourcePath,
        string destinationPath,
        ImportFileAction action,
        bool allowHardlinkFallbackToCopy)
    {
        switch (action)
        {
            case ImportFileAction.Copy:
                File.Copy(sourcePath, destinationPath, overwrite: false);
                break;

            case ImportFileAction.Hardlink:
                try
                {
                    hardLinks.CreateHardLink(sourcePath, destinationPath);
                }
                catch (CrossDeviceLinkException) when (allowHardlinkFallbackToCopy)
                {
                    File.Copy(sourcePath, destinationPath, overwrite: false);
                }

                break;

            default:
                File.Move(sourcePath, destinationPath, overwrite: false);
                break;
        }
    }
}
