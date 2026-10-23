using System.Globalization;
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
        public static Task<RecycleFolder?> FolderAsync(Func<long, Task<StorageFiles>> open, ReplacementSettingsData settings, ReplacedFileData file, CancellationToken ct) =>
            FolderAsync(open, settings, file.StorageMountId, file.RootPath, ct);

        public static async Task<RecycleFolder?> FolderAsync(Func<long, Task<StorageFiles>> open, ReplacementSettingsData settings, long storageMountId, string rootPath, CancellationToken ct)
        {
            if (settings.RecycleModeId == RecycleMode.Delete)
            {
                return null;
            }

            if (settings.RecycleModeId == RecycleMode.LibraryFolder)
            {
                return await PrepareAsync(await open(storageMountId), storageMountId, StoragePath.Combine(rootPath, StorageFiles.RecycleFolder), ct);
            }

            if (settings is not { RecycleStorageMountId: { } mountId, IsReadOnly: false, RecyclePath: { } path })
            {
                throw new NasMountException("storage_unavailable");
            }

            return await PrepareAsync(await open(mountId), mountId, path, ct);
        }

        // The relative path of the moved file inside the folder, "" when deleted, null when it stays.
        public static async Task<string?> RecycleAsync(StorageFiles library, long storageMountId, string source, RecycleFolder? folder, string relativeTarget, CancellationToken ct)
        {
            try
            {
                if (folder is null)
                {
                    await library.DeleteFileAsync(source, ct);
                    return "";
                }

                // An earlier recycled file of that name is kept beside it.
                var target = StoragePath.Combine(folder.FolderPath, relativeTarget);
                if (await folder.Files.ExistsAsync(target, ct))
                {
                    var name = StoragePath.Name(target);
                    var stamp = "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                    target = StoragePath.Combine(StoragePath.Parent(target), Path.GetFileNameWithoutExtension(name) + stamp + Path.GetExtension(name));
                }

                if (folder.StorageMountId == storageMountId)
                {
                    if (!await library.MoveAsync(source, target, ct)) return null;
                }
                else
                {
                    // Another connection gets a copy; the original goes once the copy is complete.
                    await using (var from = await library.OpenReadAsync(source, ct))
                    await using (var to = await folder.Files.CreateAsync(target, ct) ?? throw new IOException("The recycle folder already holds that name."))
                    {
                        await from.CopyToAsync(to, 1024 * 1024, ct);
                    }

                    await library.DeleteFileAsync(source, ct);
                }

                return target[(folder.FolderPath.Length == 0 ? 0 : folder.FolderPath.Length + 1)..];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NasMountException)
            {
                return null;
            }
        }

        // A recycled file leaves with the folders it leaves empty, up to the recycle folder; false when it has to wait.
        public static async Task<bool> DeleteAsync(StorageFiles files, string folderPath, string relativePath, CancellationToken ct)
        {
            try
            {
                var path = StoragePath.Combine(folderPath, relativePath);
                if (await files.ExistsAsync(path, ct))
                {
                    await files.DeleteFileAsync(path, ct);
                }

                await files.DeleteEmptyFoldersAsync(StoragePath.Parent(path), folderPath, ct);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NasMountException)
            {
                return false;
            }
        }

        // The connection itself must be there before anything is created below it.
        private static async Task<RecycleFolder> PrepareAsync(StorageFiles files, long storageMountId, string folderPath, CancellationToken ct)
        {
            if (await files.GetAsync("", ct) is null)
            {
                throw new NasMountException("storage_unavailable");
            }

            await files.CreateDirectoryAsync(folderPath, ct);
            return new RecycleFolder(files, storageMountId, StoragePath.Check(folderPath));
        }
    }

    internal sealed record RecycleFolder(StorageFiles Files, long StorageMountId, string FolderPath);
}
