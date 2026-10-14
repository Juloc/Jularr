using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.Core
{
    // Replaced library files: deleted or moved into a recycle folder, as configured; nothing is lost when a step fails.
    internal static class LibraryRecycleBin
    {
        // Where a file of this library goes; null deletes it. A configured folder that cannot be used keeps the file where it is.
        public static RecycleFolder? Folder(StorageOptions storage, ReplacementSettingsData settings, ReplacedFileData file)
        {
            if (settings.RecycleModeId == RecycleMode.Delete)
            {
                return null;
            }

            if (settings.RecycleModeId == RecycleMode.LibraryFolder)
            {
                var folderPath = file.RootPath.Length == 0 ? StorageFileSystem.RecycleFolder : file.RootPath + "/" + StorageFileSystem.RecycleFolder;
                return Prepare(storage.GetConnectionPath(file.StorageProtocolTypeId, file.StorageMountId, file.SharePath), file.StorageMountId, folderPath);
            }

            if (settings is not { RecycleStorageMountId: { } mountId, StorageProtocolTypeId: { } protocol, SharePath: { } share, IsReadOnly: false, RecyclePath: { } path })
            {
                throw new NasMountException("storage_unavailable");
            }

            return Prepare(storage.GetConnectionPath(protocol, mountId, share), mountId, path);
        }

        // The relative path of the moved file inside the folder, "" when deleted, null when it stays.
        public static string? Recycle(string source, RecycleFolder? folder, string relativeTarget)
        {
            try
            {
                if (folder is null)
                {
                    File.Delete(source);
                    return "";
                }

                // An earlier recycled file of that name is kept beside it.
                var target = Path.Combine(folder.Directory, relativeTarget.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var stamp = "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss", global::System.Globalization.CultureInfo.InvariantCulture);
                    target = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + stamp + Path.GetExtension(target));
                }

                File.Move(source, target, false);
                return Path.GetRelativePath(folder.Directory, target).Replace(Path.DirectorySeparatorChar, '/');
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // A recycled file leaves with the folders it leaves empty, up to the recycle folder; false when it has to wait.
        public static bool Delete(string folder, string relativePath)
        {
            try
            {
                var file = new FileInfo(Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (file.Exists)
                {
                    file.Delete();
                }

                for (var directory = file.Directory; directory is not null && directory.FullName.Length > folder.Length
                    && directory.FullName.StartsWith(folder, StringComparison.Ordinal) && directory.Exists && !directory.EnumerateFileSystemInfos().Any(); directory = directory.Parent)
                {
                    directory.Delete();
                }

                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // The connection itself must be there before anything is created below it.
        private static RecycleFolder Prepare(string connection, long storageMountId, string folderPath)
        {
            Directory.CreateDirectory(Path.Combine(StorageFileSystem.ResolveDirectory(connection, ""), folderPath.Replace('/', Path.DirectorySeparatorChar)));
            return new RecycleFolder(StorageFileSystem.ResolveDirectory(connection, folderPath), storageMountId, folderPath);
        }
    }

    internal sealed record RecycleFolder(string Directory, long StorageMountId, string FolderPath);
}
