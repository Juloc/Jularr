using Jularr.Backend.Data.Logic.Library;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Core
{
    // Replaced library files: moved into a recycle folder, or deleted when none is set; nothing is lost when a move fails.
    internal static class LibraryRecycleBin
    {
        // The recycle folder for a file of this library: inside it in simple mode, the configured folder, or none for deletion.
        // A configured folder that cannot be used keeps the file where it is.
        public static string? Folder(StorageOptions storage, ReplacementSettingsData settings, string libraryDirectory)
        {
            if (settings.RecyclesInLibraries)
            {
                return Path.Combine(libraryDirectory, StorageFileSystem.RecycleFolder);
            }

            if (settings.RecycleStorageMountId is null)
            {
                return null;
            }

            if (settings is not { RecycleStorageMountId: { } mountId, StorageProtocolTypeId: { } protocol, SharePath: { } share, IsReadOnly: false, RecyclePath: { } path })
            {
                throw new NasMountException("storage_unavailable");
            }

            // The connection itself must be there before anything is created below it.
            var connection = storage.GetConnectionPath(protocol, mountId, share);
            Directory.CreateDirectory(Path.Combine(StorageFileSystem.ResolveDirectory(connection, ""), path.Replace('/', Path.DirectorySeparatorChar)));
            return StorageFileSystem.ResolveDirectory(connection, path);
        }

        // False when the file stays where it is.
        public static bool Recycle(string source, string? recycleDirectory, string relativeTarget)
        {
            string target;
            try
            {
                if (recycleDirectory is null)
                {
                    File.Delete(source);
                    return true;
                }

                // The time of recycling decides when it leaves; an earlier file of that name is kept beside it.
                target = Path.Combine(recycleDirectory, relativeTarget.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    target = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + Path.GetExtension(target));
                }

                File.Move(source, target, false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            try
            {
                File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Moved all the same; it leaves by its own time.
            }

            return true;
        }

        // Files recycled longer ago than the given days leave, then the folders they leave empty.
        public static void Clean(string recycleDirectory, int days, DateTime now)
        {
            if (days <= 0 || !Directory.Exists(recycleDirectory))
            {
                return;
            }

            // What cannot go now is tried again on the next pass.
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var file in new DirectoryInfo(recycleDirectory).EnumerateFiles("*", options).Where(file => file.LastWriteTimeUtc < now.AddDays(-days)))
            {
                Try(file.Delete);
            }

            foreach (var directory in new DirectoryInfo(recycleDirectory).EnumerateDirectories("*", options).OrderByDescending(directory => directory.FullName.Length))
            {
                Try(() =>
                {
                    if (!directory.EnumerateFileSystemInfos().Any())
                    {
                        directory.Delete();
                    }
                });
            }
        }

        private static void Try(Action delete)
        {
            try
            {
                delete();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
