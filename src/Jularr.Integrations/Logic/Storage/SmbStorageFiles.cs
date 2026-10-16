using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Data.Storage;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Jularr.Integrations.Logic.Storage
{
    // A folder of an SMB share Jularr reaches itself, without a mount.
    internal sealed class SmbStorageFiles(SmbShare share, string basePath) : StorageFiles
    {
        private const ShareAccess Shared = ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete;
        private readonly string _basePath = StoragePath.Check(basePath);

        public override Task<StorageEntry?> GetAsync(string path, CancellationToken ct) => share.RunAsync(store =>
        {
            var status = store.CreateFile(out var handle, out _, Map(path), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, 0, Shared,
                CreateDisposition.FILE_OPEN, 0, null);
            if (status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE)
            {
                return null;
            }

            SmbShare.Check(status);
            try
            {
                SmbShare.Check(store.GetFileInformation(out var information, handle, FileInformationClass.FileNetworkOpenInformation));
                var open = (FileNetworkOpenInformation)information;
                return open.FileAttributes.HasFlag(FileAttributes.ReparsePoint) ? null
                    : new StorageEntry(StoragePath.Check(path).Length == 0 ? "" : StoragePath.Name(path), open.FileAttributes.HasFlag(FileAttributes.Directory), open.EndOfFile,
                        (open.LastWriteTime ?? DateTime.MinValue).ToUniversalTime());
            }
            finally
            {
                store.CloseFile(handle);
            }
        }, ct);

        public override Task<IReadOnlyList<StorageEntry>> ListAsync(string directory, CancellationToken ct) => share.RunAsync<IReadOnlyList<StorageEntry>>(store =>
        {
            SmbShare.Check(store.CreateFile(out var handle, out _, Map(directory), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Directory, Shared,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null));
            try
            {
                var status = store.QueryDirectory(out var entries, handle, "*", FileInformationClass.FileDirectoryInformation);
                if (status != NTStatus.STATUS_NO_MORE_FILES) SmbShare.Check(status);
                return (entries ?? []).OfType<FileDirectoryInformation>()
                    .Where(entry => entry.FileName is not ("." or "..")
                        && (entry.FileAttributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                    .Select(entry => new StorageEntry(entry.FileName, entry.FileAttributes.HasFlag(FileAttributes.Directory), entry.EndOfFile, entry.LastWriteTime.ToUniversalTime()))
                    .ToArray();
            }
            finally
            {
                store.CloseFile(handle);
            }
        }, ct);

        public override Task<Stream> OpenReadAsync(string path, CancellationToken ct) => share.RunAsync<Stream>(store =>
        {
            SmbShare.Check(store.CreateFile(out var handle, out _, Map(path), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null));
            try
            {
                SmbShare.Check(store.GetFileInformation(out var information, handle, FileInformationClass.FileNetworkOpenInformation));
                return new SmbFileStream(share, handle, ((FileNetworkOpenInformation)information).EndOfFile, false);
            }
            catch
            {
                store.CloseFile(handle);
                throw;
            }
        }, ct);

        public override Task<Stream?> CreateAsync(string path, CancellationToken ct) => share.RunAsync<Stream?>(store =>
        {
            CreateParents(store, path);
            var status = store.CreateFile(out var handle, out _, Map(path), AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None,
                CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            if (status == NTStatus.STATUS_OBJECT_NAME_COLLISION)
            {
                return null;
            }

            SmbShare.Check(status);
            return new SmbFileStream(share, handle, 0, true);
        }, ct);

        public override Task<bool> MoveAsync(string source, string target, CancellationToken ct) => share.RunAsync(store =>
        {
            CreateParents(store, target);
            SmbShare.Check(store.CreateFile(out var handle, out _, Map(source), AccessMask.DELETE | AccessMask.SYNCHRONIZE, 0, Shared, CreateDisposition.FILE_OPEN, 0, null));
            try
            {
                var status = store.SetFileInformation(handle, new FileRenameInformationType2 { FileName = Map(target), ReplaceIfExists = false });
                if (status == NTStatus.STATUS_OBJECT_NAME_COLLISION)
                {
                    return false;
                }

                SmbShare.Check(status);
                return true;
            }
            finally
            {
                store.CloseFile(handle);
            }
        }, ct);

        public override Task DeleteFileAsync(string path, CancellationToken ct) => share.RunAsync(store =>
        {
            Delete(store, path, CreateOptions.FILE_NON_DIRECTORY_FILE);
            return true;
        }, ct);

        public override Task<bool> DeleteDirectoryAsync(string path, CancellationToken ct) => share.RunAsync(store =>
        {
            var status = store.CreateFile(out var handle, out _, Map(path), AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Directory, Shared,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
            if (status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND)
            {
                return true;
            }

            SmbShare.Check(status);
            try
            {
                status = store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
                if (status == NTStatus.STATUS_DIRECTORY_NOT_EMPTY)
                {
                    return false;
                }

                SmbShare.Check(status);
                return true;
            }
            finally
            {
                store.CloseFile(handle);
            }
        }, ct);

        public override Task CreateDirectoryAsync(string path, CancellationToken ct) => share.RunAsync(store =>
        {
            CreateDirectory(store, StoragePath.Check(path));
            return true;
        }, ct);

        public override async Task<StorageProbe> ProbeAsync(CancellationToken ct)
        {
            try
            {
                return await share.RunAsync(store =>
                {
                    // The folder answers, and the share tells its space when it can.
                    SmbShare.Check(store.CreateFile(out var handle, out _, Map(""), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Directory, Shared,
                        CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null));
                    store.CloseFile(handle);
                    if (store.GetFileSystemInformation(out var information, FileSystemInformationClass.FileFsFullSizeInformation) == NTStatus.STATUS_SUCCESS
                        && information is FileFsFullSizeInformation size)
                    {
                        var unit = (long)size.SectorsPerAllocationUnit * size.BytesPerSector;
                        return new StorageProbe(true, null, size.CallerAvailableAllocationUnits * unit, size.TotalAllocationUnits * unit);
                    }

                    return new StorageProbe(true, null, null, null);
                }, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException)
            {
                return new StorageProbe(false, "probe_timeout", null, null);
            }
            catch (UnauthorizedAccessException)
            {
                return new StorageProbe(false, "permission_denied", null, null);
            }
            catch (Exception exception) when (exception is NasMountException or IOException)
            {
                return new StorageProbe(false, "storage_unavailable", null, null);
            }
        }

        // Share paths use backslashes and start at the share's root.
        private string Map(string path) => StoragePath.Combine(_basePath, path).Replace('/', '\\');

        private void CreateParents(ISMBFileStore store, string path) => CreateDirectory(store, StoragePath.Parent(path));

        private void CreateDirectory(ISMBFileStore store, string path)
        {
            var current = "";
            foreach (var part in path.Length == 0 ? [] : path.Split('/'))
            {
                current = current.Length == 0 ? part : current + "/" + part;
                SmbShare.Check(store.CreateFile(out var handle, out _, Map(current), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Directory, Shared,
                    CreateDisposition.FILE_OPEN_IF, CreateOptions.FILE_DIRECTORY_FILE, null));
                store.CloseFile(handle);
            }

            // The connection's own folder has to be there already.
            if (path.Length == 0)
            {
                SmbShare.Check(store.CreateFile(out var handle, out _, Map(""), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Directory, Shared,
                    CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null));
                store.CloseFile(handle);
            }
        }

        private void Delete(ISMBFileStore store, string path, CreateOptions options)
        {
            SmbShare.Check(store.CreateFile(out var handle, out _, Map(path), AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, Shared,
                CreateDisposition.FILE_OPEN, options, null));
            try
            {
                SmbShare.Check(store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true }));
            }
            finally
            {
                store.CloseFile(handle);
            }
        }
    }
}
