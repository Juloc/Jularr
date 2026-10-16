using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Jularr.Backend.Tests
{
    // An SMB share over a temporary folder, answering like a server does: NT statuses, a delete on close, renames that never replace,
    // and dot names hidden as Samba shows them. No network; SMBLibrary's own client only speaks to port 445.
    internal sealed class FakeSmbStore(string root, uint maxReadSize = 1000) : ISMBFileStore
    {
        private sealed class Handle(string path, bool isDirectory, FileStream? stream)
        {
            public string Path { get; set; } = path;
            public bool IsDirectory { get; } = isDirectory;
            public FileStream? Stream { get; } = stream;
            public bool DeleteOnClose { get; set; }
        }

        public uint MaxReadSize => maxReadSize;
        public uint MaxWriteSize => 4096;

        // Set to drop the session the next time the store is used.
        public bool IsLost { get; set; }

        public int OpenHandles { get; private set; }

        public NTStatus CreateFile(out object handle, out FileStatus fileStatus, string path, AccessMask desiredAccess, FileAttributes fileAttributes, ShareAccess shareAccess,
            CreateDisposition createDisposition, CreateOptions createOptions, SecurityContext securityContext)
        {
            CheckSession();
            handle = null!;
            fileStatus = FileStatus.FILE_DOES_NOT_EXIST;
            var full = Full(path);
            if (!Directory.Exists(Path.GetDirectoryName(full)) && full != root)
            {
                return NTStatus.STATUS_OBJECT_PATH_NOT_FOUND;
            }

            var isDirectory = Directory.Exists(full);
            var exists = isDirectory || File.Exists(full);
            var wantsDirectory = createOptions.HasFlag(CreateOptions.FILE_DIRECTORY_FILE);
            if (exists && createDisposition == CreateDisposition.FILE_CREATE)
            {
                return NTStatus.STATUS_OBJECT_NAME_COLLISION;
            }

            if (!exists && createDisposition == CreateDisposition.FILE_OPEN)
            {
                return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
            }

            if (exists && wantsDirectory && !isDirectory)
            {
                return NTStatus.STATUS_NOT_A_DIRECTORY;
            }

            if (exists && createOptions.HasFlag(CreateOptions.FILE_NON_DIRECTORY_FILE) && isDirectory)
            {
                return NTStatus.STATUS_FILE_IS_A_DIRECTORY;
            }

            if (!exists)
            {
                if (wantsDirectory) Directory.CreateDirectory(full);
                else File.Create(full).Dispose();
                isDirectory = wantsDirectory;
                fileStatus = FileStatus.FILE_CREATED;
            }
            else
            {
                fileStatus = FileStatus.FILE_OPENED;
            }

            var stream = isDirectory || !(desiredAccess.HasFlag(AccessMask.GENERIC_READ) || desiredAccess.HasFlag(AccessMask.GENERIC_WRITE)) ? null
                : new FileStream(full, FileMode.Open, desiredAccess.HasFlag(AccessMask.GENERIC_WRITE) ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            handle = new Handle(full, isDirectory, stream);
            OpenHandles++;
            return NTStatus.STATUS_SUCCESS;
        }

        public NTStatus CloseFile(object handle)
        {
            var open = (Handle)handle;
            open.Stream?.Dispose();
            OpenHandles--;
            if (open.DeleteOnClose)
            {
                if (open.IsDirectory) Directory.Delete(open.Path);
                else File.Delete(open.Path);
            }

            return NTStatus.STATUS_SUCCESS;
        }

        public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount)
        {
            CheckSession();
            var stream = ((Handle)handle).Stream!;
            stream.Position = offset;
            var buffer = new byte[Math.Min(maxCount, (int)MaxReadSize)];
            var read = stream.Read(buffer);
            data = buffer[..read];
            return read == 0 ? NTStatus.STATUS_END_OF_FILE : NTStatus.STATUS_SUCCESS;
        }

        public NTStatus WriteFile(out int numberOfBytesWritten, object handle, long offset, byte[] data)
        {
            CheckSession();
            var stream = ((Handle)handle).Stream!;
            stream.Position = offset;
            stream.Write(data);
            numberOfBytesWritten = data.Length;
            return NTStatus.STATUS_SUCCESS;
        }

        public NTStatus QueryDirectory(out List<QueryDirectoryFileInformation> result, object handle, string fileName, FileInformationClass informationClass)
        {
            CheckSession();
            var directory = new DirectoryInfo(((Handle)handle).Path);
            result = [Entry(directory, "."), Entry(directory, "..")];
            result.AddRange(directory.EnumerateFileSystemInfos().Select(info => Entry(info, info.Name)));
            return NTStatus.STATUS_NO_MORE_FILES;
        }

        public NTStatus GetFileInformation(out FileInformation result, object handle, FileInformationClass informationClass)
        {
            CheckSession();
            var open = (Handle)handle;
            FileSystemInfo info = open.IsDirectory ? new DirectoryInfo(open.Path) : new FileInfo(open.Path);
            result = new FileNetworkOpenInformation
            {
                EndOfFile = info is FileInfo file ? file.Length : 0,
                AllocationSize = info is FileInfo sized ? sized.Length : 0,
                FileAttributes = Attributes(info),
                LastWriteTime = info.LastWriteTimeUtc,
                CreationTime = info.CreationTimeUtc,
                LastAccessTime = info.LastAccessTimeUtc,
                ChangeTime = info.LastWriteTimeUtc
            };
            return NTStatus.STATUS_SUCCESS;
        }

        public NTStatus SetFileInformation(object handle, FileInformation information)
        {
            CheckSession();
            var open = (Handle)handle;
            switch (information)
            {
                case FileRenameInformationType2 rename:
                    var target = Full(rename.FileName);
                    if (File.Exists(target) || Directory.Exists(target)) return NTStatus.STATUS_OBJECT_NAME_COLLISION;
                    if (open.IsDirectory) Directory.Move(open.Path, target);
                    else File.Move(open.Path, target);
                    open.Path = target;
                    return NTStatus.STATUS_SUCCESS;
                case FileDispositionInformation { DeletePending: true }:
                    if (open.IsDirectory && Directory.EnumerateFileSystemEntries(open.Path).Any()) return NTStatus.STATUS_DIRECTORY_NOT_EMPTY;
                    open.DeleteOnClose = true;
                    return NTStatus.STATUS_SUCCESS;
                default:
                    return NTStatus.STATUS_NOT_SUPPORTED;
            }
        }

        public NTStatus GetFileSystemInformation(out FileSystemInformation result, FileSystemInformationClass informationClass)
        {
            CheckSession();
            result = new FileFsFullSizeInformation
            {
                TotalAllocationUnits = 1000, CallerAvailableAllocationUnits = 250, ActualAvailableAllocationUnits = 250, SectorsPerAllocationUnit = 8, BytesPerSector = 512
            };
            return NTStatus.STATUS_SUCCESS;
        }

        public NTStatus Disconnect() => NTStatus.STATUS_SUCCESS;

        public NTStatus FlushFileBuffers(object handle) => NTStatus.STATUS_SUCCESS;
        public NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock) => throw new NotSupportedException();
        public NTStatus UnlockFile(object handle, long byteOffset, long length) => throw new NotSupportedException();
        public NTStatus SetFileSystemInformation(FileSystemInformation information) => throw new NotSupportedException();
        public NTStatus GetSecurityInformation(out SecurityDescriptor result, object handle, SecurityInformation securityInformation) => throw new NotSupportedException();
        public NTStatus SetSecurityInformation(object handle, SecurityInformation securityInformation, SecurityDescriptor securityDescriptor) => throw new NotSupportedException();

        public NTStatus NotifyChange(out object ioRequest, object handle, NotifyChangeFilter completionFilter, bool watchTree, int outputBufferSize,
            OnNotifyChangeCompleted onNotifyChangeCompleted, object context) => throw new NotSupportedException();

        public NTStatus Cancel(object ioRequest) => throw new NotSupportedException();
        public NTStatus DeviceIOControl(object handle, uint ctlCode, byte[] input, out byte[] output, int maxOutputLength) => throw new NotSupportedException();

        private void CheckSession()
        {
            // SMBLibrary reports a dropped connection as an invalid operation.
            if (IsLost) throw new InvalidOperationException("The connection was closed.");
        }

        private string Full(string path) => path.Length == 0 ? root : Path.Combine(root, path.Replace('\\', Path.DirectorySeparatorChar));

        private static FileAttributes Attributes(FileSystemInfo info) =>
            (info is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal) | (info.Name.StartsWith('.') ? FileAttributes.Hidden : 0);

        private static FileDirectoryInformation Entry(FileSystemInfo info, string name) => new()
        {
            FileName = name,
            FileAttributes = Attributes(info) & ~(name is "." or ".." ? FileAttributes.Hidden : 0),
            EndOfFile = info is FileInfo file ? file.Length : 0,
            LastWriteTime = info.LastWriteTimeUtc,
            CreationTime = info.CreationTimeUtc,
            LastAccessTime = info.LastAccessTimeUtc,
            ChangeTime = info.LastWriteTimeUtc
        };
    }
}
