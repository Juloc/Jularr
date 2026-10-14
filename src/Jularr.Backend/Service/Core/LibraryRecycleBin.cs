using Jularr.Backend.Data.Logic.Library;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Core
{
    // Replaced library files: moved into a recycle folder, or deleted when none is set; nothing is lost when a move fails.
    internal static class LibraryRecycleBin
    {
        // The recycle folder for a file of this library: inside it in simple mode, the configured folder, or none for deletion.
        public static string? Folder(StorageOptions storage, ReplacementSettingsData settings, string libraryDirectory)
        {
            if (settings.RecyclesInLibraries)
            {
                return Path.Combine(libraryDirectory, StorageFileSystem.RecycleFolder);
            }

            if (settings is not { RecycleStorageMountId: { } mountId, StorageProtocolTypeId: { } protocol, SharePath: { } share, RecyclePath: { } path })
            {
                return null;
            }

            var connection = storage.GetConnectionPath(protocol, mountId, share);
            Directory.CreateDirectory(Path.Combine(connection, path.Replace('/', Path.DirectorySeparatorChar)));
            return StorageFileSystem.ResolveDirectory(connection, path);
        }

        // False when the file stays where it is.
        public static bool Recycle(string source, string? recycleDirectory, string relativeTarget)
        {
            try
            {
                if (!File.Exists(source))
                {
                    return true;
                }

                if (recycleDirectory is null)
                {
                    File.Delete(source);
                    return true;
                }

                // The time of recycling decides when it leaves; an earlier file of that name is kept beside it.
                var target = Path.Combine(recycleDirectory, relativeTarget.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    target = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + Path.GetExtension(target));
                }

                File.Move(source, target, false);
                File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NasMountException)
            {
                return false;
            }
        }

        // Files recycled longer ago than the given days leave, then the folders they leave empty.
        public static void Clean(string recycleDirectory, int days, DateTime now)
        {
            if (days <= 0 || !Directory.Exists(recycleDirectory))
            {
                return;
            }

            try
            {
                foreach (var file in new DirectoryInfo(recycleDirectory).EnumerateFiles("*", SearchOption.AllDirectories).Where(file => file.LastWriteTimeUtc < now.AddDays(-days)))
                {
                    file.Delete();
                }

                foreach (var directory in new DirectoryInfo(recycleDirectory).EnumerateDirectories("*", SearchOption.AllDirectories).OrderByDescending(directory => directory.FullName.Length))
                {
                    if (!directory.EnumerateFileSystemInfos().Any())
                    {
                        directory.Delete();
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The next pass tries again.
            }
        }
    }
}
