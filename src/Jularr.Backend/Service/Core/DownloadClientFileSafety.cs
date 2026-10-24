using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Core
{
    // Never follows symbolic links, never deletes a root, and refuses paths owned by a known SABnzbd job.
    internal static class DownloadClientFileSafety
    {
        internal const int PageSize = 200;
        internal const int MaxFilesToDelete = 50000;

        internal static string Root(string area, string? configuredRemote, string? configuredLocal, string? providerRemote)
        {
            if (area is not ("complete" or "incomplete") || string.IsNullOrWhiteSpace(configuredLocal)
                || !Path.IsPathFullyQualified(configuredLocal) || string.IsNullOrWhiteSpace(configuredRemote)
                || string.IsNullOrWhiteSpace(providerRemote))
                throw new DataValidationException(nameof(area), "invalid_transition");

            // A SAB category's completed directory can be narrower than the server-wide completedir.
            // In that case expose only the configured category root, never its unmounted parent.
            var relative = ImportPlacementLogic.MapRemotePath(providerRemote, configuredRemote);
            if (relative is null && ImportPlacementLogic.MapRemotePath(configuredRemote, providerRemote) is not null)
                relative = "";
            if (relative is null)
            {
                throw new DataValidationException(nameof(area), "invalid_transition");
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredLocal));
            if (string.Equals(root, Path.GetPathRoot(root), StringComparison.Ordinal) || !Directory.Exists(root))
                throw new DataValidationException(nameof(area), "invalid_transition");
            return SafePath(root, relative, allowRoot: true);
        }

        // The configured path is relative to the selected Jularr storage mount, not a host pathname.
        internal static string StorageRoot(string area, string? configuredRemote, string? localPath, string? providerRemote)
        {
            if (area is not ("complete" or "incomplete") || string.IsNullOrWhiteSpace(configuredRemote)
                || string.IsNullOrWhiteSpace(providerRemote) || string.IsNullOrWhiteSpace(localPath)
                || localPath.StartsWith('/') || localPath.Contains('\\'))
                throw new DataValidationException(nameof(localPath), "invalid_path");

            var relative = ImportPlacementLogic.MapRemotePath(providerRemote, configuredRemote);
            if (relative is null && ImportPlacementLogic.MapRemotePath(configuredRemote, providerRemote) is not null)
                relative = "";
            if (relative is null)
            {
                throw new DataValidationException(nameof(configuredRemote), "invalid_path");
            }

            try
            {
                return StoragePath.Combine(StoragePath.Check(localPath), StoragePath.Check(relative));
            }
            catch (Jularr.Integrations.Data.Nas.NasMountException)
            {
                throw new DataValidationException(nameof(localPath), "invalid_path");
            }
        }

        internal static string StorageRelative(string? path, bool allowRoot = false)
        {
            if (string.IsNullOrEmpty(path))
            {
                if (allowRoot)
                {
                    return "";
                }
                throw new DataValidationException(nameof(path), "invalid_path");
            }

            if (path.StartsWith('/') || path.EndsWith('/') || path.Contains('\\') || path.Contains(':')
                || path.Any(char.IsControl) || path.Split('/').Any(segment => segment is "." or ".." or ""))
                throw new DataValidationException(nameof(path), "invalid_path");
            return path;
        }

        internal static string SafePath(string root, string? relative, bool allowRoot = false)
        {
            var segments = (relative ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(part => part is "." or ".." || part.Contains(':') || part.Any(char.IsControl))
                || segments.Length == 0 && !allowRoot)
                throw new DataValidationException(nameof(relative), "invalid_transition");

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var path = Path.GetFullPath(segments.Aggregate(root, Path.Combine));
            if (!string.Equals(path, root, comparison) && !path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new DataValidationException(nameof(relative), "invalid_transition");

            var check = root;
            AssertNotLink(check);
            foreach (var segment in segments)
            {
                check = Path.Combine(check, segment);
                AssertNotLink(check);
            }
            return path;
        }

        private static void AssertNotLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new DataValidationException("RelativePath", "invalid_transition");
        }

        internal static DownloadClientBrowseResultV1 Browse(string area, string root, string? relative, int page, SabnzbdConsoleSnapshot snapshot)
        {
            if (page is < 1 or > 10000)
            {
                throw new DataValidationException(nameof(page), "invalid_transition");
            }
            var directory = SafePath(root, relative, allowRoot: true);
            if (!Directory.Exists(directory))
            {
                throw new DataValidationException(nameof(relative), "invalid_transition");
            }

            var names = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Skip((page - 1) * PageSize).Take(PageSize + 1).ToArray();
            var entries = new List<DownloadClientFileV1>();
            foreach (var name in names.Take(PageSize))
            {
                var sub = string.IsNullOrWhiteSpace(relative) ? name! : relative.TrimEnd('/', '\\') + "/" + name;
                try
                {
                    var full = SafePath(root, sub);
                    var attr = File.GetAttributes(full);
                    var isDirectory = (attr & FileAttributes.Directory) != 0;
                    entries.Add(new DownloadClientFileV1(name!, sub, isDirectory, isDirectory ? null : new FileInfo(full).Length,
                        DownloadFileOwnershipLogic.Assignment(area, sub, snapshot)));
                }
                catch (Exception exception) when (exception is DataValidationException or IOException or UnauthorizedAccessException)
                {
                    // A disappearing or linked entry must not hide the rest of the directory.
                }
            }
            return new DownloadClientBrowseResultV1(area, root, relative ?? "", names.Length > PageSize, new BaseDataList<DownloadClientFileV1>(entries));
        }

        internal static DownloadClientDeletionPreviewV1 Preview(string area, string root, string relative, SabnzbdConsoleSnapshot snapshot)
        {
            if (DownloadFileOwnershipLogic.Assignment(area, relative, snapshot) != "orphan"
                || area != "incomplete" || relative.Contains("/") || relative.Contains("\\"))
                throw new DataValidationException(nameof(relative), "invalid_transition");

            var full = SafePath(root, relative);
            return InspectTree(area, root, relative, full);
        }

        private static DownloadClientDeletionPreviewV1 InspectTree(string area, string root, string relative, string full)
        {
            var stack = new Stack<string>();
            stack.Push(full);
            long bytes = 0;
            var count = 0;
            var visited = 0;
            var facts = new StringBuilder();
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                var attrs = File.GetAttributes(next);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    throw new DataValidationException(nameof(relative), "invalid_transition");
                }
                visited++;
                // Fingerprints keep the same logical job name after atomic quarantine.
                // This lets us detect changes to the folder before deleting its contents.
                facts.Append(next == full ? relative : Path.Combine(relative, Path.GetRelativePath(full, next)))
                    .Append('|').Append((int)attrs).Append('|');
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(next).OrderByDescending(item => item, StringComparer.Ordinal)) stack.Push(child);
                }
                else
                {
                    var file = new FileInfo(next);
                    checked
                    {
                        bytes += file.Length;
                    }
                    facts.Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks);
                    count++;
                }
                facts.Append('\n');
                if (visited + stack.Count > MaxFilesToDelete)
                    throw new DataValidationException(nameof(relative), "invalid_transition");
            }
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(facts.ToString())));
            return new DownloadClientDeletionPreviewV1(area, relative, count, bytes, fingerprint);
        }

        // Manual orphan deletion atomically detaches the directory from SABnzbd's
        // incomplete root. A changed inventory is restored and never deleted.
        // No active job, history entry or unverified filesystem root is eligible.
        internal static void DeleteConfirmed(string area, string root, string relative, string fingerprint, SabnzbdConsoleSnapshot snapshot)
        {
            if (fingerprint.Length != 64 || fingerprint.Any(character => !Uri.IsHexDigit(character)))
                throw new DataValidationException(nameof(fingerprint), "invalid_transition");

            var preview = Preview(area, root, relative, snapshot);
            if (!MatchesFingerprint(preview.Fingerprint, fingerprint))
                throw new DataValidationException(nameof(fingerprint), "invalid_transition");

            var original = SafePath(root, relative);
            if (!Directory.Exists(original))
                throw new DataValidationException(nameof(relative), "invalid_transition");

            var quarantine = ".jularr-delete-" + Guid.NewGuid().ToString("N");
            var quarantined = Path.Combine(root, quarantine);
            Directory.Move(original, quarantined);
            try
            {
                // A client may have written to the folder after the initial preview.
                // Verify the moved, now detached tree before any destructive operation.
                var staged = InspectTree(area, root, relative, quarantined);
                if (!MatchesFingerprint(staged.Fingerprint, fingerprint))
                    throw new DataValidationException(nameof(fingerprint), "invalid_transition");
            }
            catch
            {
                if (Directory.Exists(quarantined) && !Directory.Exists(original))
                    Directory.Move(quarantined, original);
                throw;
            }

            // On deletion failure leave the partial data in quarantine, never
            // restore a partially deleted folder to the provider's live path.
            Delete(root, quarantine);
        }

        private static bool MatchesFingerprint(string expected, string actual) =>
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));

        // Automatic post-import cleanup deletes only the specific provider-reported output,
        // never its mapped root or a sibling; symlinks and overly large trees are refused.
        internal static void DeleteVerifiedCompleted(string root, string relative)
        {
            // A move-mode import can already have removed the source before cleanup runs.
            // Validate containment before checking whether that target still exists.
            if (string.IsNullOrWhiteSpace(relative) || relative.Split('/', '\\').Any(x => x is "." or ".." or ""))
                throw new DataValidationException(nameof(relative), "invalid_path");
            var targetPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!targetPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
                throw new DataValidationException(nameof(relative), "invalid_path");
            if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
            {
                return;
            }
            var target = SafePath(root, relative);

            var pending = new Stack<string>();
            pending.Push(target);
            var checkedEntries = 0;
            while (pending.TryPop(out var item))
            {
                var full = SafePath(root, Path.GetRelativePath(root, item));
                var attrs = File.GetAttributes(full);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new DataValidationException(nameof(relative), "invalid_path");
                if (++checkedEntries > MaxFilesToDelete)
                    throw new DataValidationException(nameof(relative), "invalid_transition");
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(full))
                        pending.Push(child);
                }
            }

            Delete(root, relative);
        }

        internal static void Delete(string root, string relative)
        {
            var target = SafePath(root, relative);
            void DeleteEntry(string item)
            {
                var path = SafePath(root, Path.GetRelativePath(root, item));
                if (Directory.Exists(path))
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteEntry(child);
                    Directory.Delete(path);
                }
                else File.Delete(path);
            }
            DeleteEntry(target);
        }
    }
}
