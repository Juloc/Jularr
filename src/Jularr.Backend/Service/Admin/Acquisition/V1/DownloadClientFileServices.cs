using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Storage;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Logic.Sabnzbd;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    public sealed class DownloadClientFolderRequestV1 : BaseData
    {
        public DownloadClientFolderRequestV1(string area, string? relativePath = null, int pageNumber = 1, bool confirmed = false, string? fingerprint = null)
        {
            Area = area;
            RelativePath = relativePath;
            PageNumber = pageNumber;
            Confirmed = confirmed;
            Fingerprint = fingerprint;
        }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Folder))]
        public string Area { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Path))]
        public string? RelativePath { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Page))]
        public int PageNumber { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Flag))]
        public bool Confirmed { get; }

        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Fingerprint))]
        public string? Fingerprint { get; }
    }

    public sealed class DownloadClientFileV1 : BaseResponse
    {
        public DownloadClientFileV1(string name, string relativePath, bool isDirectory, long? sizeBytes, string assignment)
        {
            Name = name; RelativePath = relativePath; IsDirectory = isDirectory; SizeBytes = sizeBytes; Assignment = assignment;
        }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.FileName))]
        public string Name { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Path))]
        public string RelativePath { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Flag))]
        public bool IsDirectory { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Bytes))]
        public long? SizeBytes { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Assignment))]
        public string Assignment { get; }
    }

    public sealed class DownloadClientBrowseResultV1 : BaseResponse
    {
        public DownloadClientBrowseResultV1(string area, string root, string relativePath, bool hasMore, BaseDataList<DownloadClientFileV1> entries)
        {
            Area = area; Root = root; RelativePath = relativePath; HasMore = hasMore; Entries = entries;
        }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Folder))]
        public string Area { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Path))]
        public string Root { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Path))]
        public string RelativePath { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Flag))]
        public bool HasMore { get; }
        public BaseDataList<DownloadClientFileV1> Entries { get; }
    }

    public sealed class DownloadClientDeletionPreviewV1 : BaseResponse
    {
        public DownloadClientDeletionPreviewV1(string area, string relativePath, int files, long bytes, string fingerprint)
        {
            Area = area; RelativePath = relativePath; Files = files; Bytes = bytes; Fingerprint = fingerprint;
        }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Folder))]
        public string Area { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Path))]
        public string RelativePath { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Page))]
        public int Files { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Bytes))]
        public long Bytes { get; }
        [DataField(typeof(DownloadClientConsoleFields), nameof(DownloadClientConsoleFields.Fingerprint))]
        public string Fingerprint { get; }
    }

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
            if (relative is null) throw new DataValidationException(nameof(area), "invalid_transition");

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
            if (relative is null) throw new DataValidationException(nameof(configuredRemote), "invalid_path");

            try { return StoragePath.Combine(StoragePath.Check(localPath), StoragePath.Check(relative)); }
            catch (Jularr.Integrations.Data.Nas.NasMountException)
            {
                throw new DataValidationException(nameof(localPath), "invalid_path");
            }
        }

        internal static string StorageRelative(string? path, bool allowRoot = false)
        {
            if (string.IsNullOrEmpty(path))
            {
                if (allowRoot) return "";
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

        internal static string Assignment(string area, string relative, SabnzbdConsoleSnapshot snapshot)
        {
            if (snapshot.Queue.Count >= 200 || snapshot.History.Count >= 200) return "unchecked";
            var top = relative.Replace('\\', '/').Split('/')[0];
            var queue = snapshot.Queue.Any(job => string.Equals(job.Name, top, StringComparison.OrdinalIgnoreCase));
            var history = snapshot.History.Any(job => string.Equals(Path.GetFileName(job.StoragePath?.TrimEnd('/', '\\')), top, StringComparison.OrdinalIgnoreCase));
            if (queue) return "queue";
            if (history) return "history";
            return area == "incomplete" && snapshot.OrphanFolders.Contains(top, StringComparer.OrdinalIgnoreCase) ? "orphan" : "unassigned";
        }

        internal static DownloadClientBrowseResultV1 Browse(string area, string root, string? relative, int page, SabnzbdConsoleSnapshot snapshot)
        {
            if (page is < 1 or > 10000) throw new DataValidationException(nameof(page), "invalid_transition");
            var directory = SafePath(root, relative, allowRoot: true);
            if (!Directory.Exists(directory)) throw new DataValidationException(nameof(relative), "invalid_transition");

            var names = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Skip((page - 1) * PageSize).Take(PageSize + 1).ToArray();
            var entries = new List<DownloadClientFileV1>();
            foreach (var name in names.Take(PageSize))
            {
                var sub = string.IsNullOrWhiteSpace(relative) ? name! : relative.TrimEnd('/', '\\') + "/" + name;
                var full = SafePath(root, sub);
                var attr = File.GetAttributes(full);
                var isDirectory = (attr & FileAttributes.Directory) != 0;
                entries.Add(new DownloadClientFileV1(name!, sub, isDirectory, isDirectory ? null : new FileInfo(full).Length, Assignment(area, sub, snapshot)));
            }
            return new DownloadClientBrowseResultV1(area, root, relative ?? "", names.Length > PageSize, new BaseDataList<DownloadClientFileV1>(entries));
        }

        internal static DownloadClientDeletionPreviewV1 Preview(string area, string root, string relative, SabnzbdConsoleSnapshot snapshot)
        {
            if (Assignment(area, relative, snapshot) is not ("orphan" or "unassigned"))
                throw new DataValidationException(nameof(relative), "invalid_transition");

            var full = SafePath(root, relative);
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
                if ((attrs & FileAttributes.ReparsePoint) != 0) throw new DataValidationException(nameof(relative), "invalid_transition");
                visited++;
                facts.Append(Path.GetRelativePath(root, next)).Append('|').Append((int)attrs).Append('|');
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(next).OrderByDescending(item => item, StringComparer.Ordinal)) stack.Push(child);
                }
                else
                {
                    var file = new FileInfo(next);
                    checked { bytes += file.Length; }
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

    internal abstract class DownloadClientFilesBase(SabnzbdClient client, IDataProtectionProvider protection, StorageAccess storage)
    {
        protected readonly SabnzbdClient Client = client;
        private readonly StorageAccess _storage = storage;
        private readonly IDataProtector _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);

        protected static void CheckArea(string area)
        {
            if (area is not ("complete" or "incomplete"))
                throw new DataValidationException(nameof(area), "invalid_transition");
        }

        protected async Task<(string Root, SabnzbdConsoleSnapshot Snapshot, StorageFiles? NetworkFiles, string? NetworkRoot, bool ReadOnly)> OpenAsync(
            LogicContext context, long connectionId, string area, CancellationToken ct)
        {
            CheckArea(area);
            var connection = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAcquisitionConnectionAsync(connectionId, token), ct);
            if (connection.AcquisitionConnectionTypeId != AcquisitionConnectionType.Sabnzbd)
                throw new DataValidationException(nameof(connectionId), "invalid_transition");

            var api = new SabnzbdConnection(connection.HttpAddress, AcquisitionConnectionInput.Unprotect(_protector, connection.ProtectedApiKey));
            var snapshot = await Client.ReadConsoleAsync(api, ct);
            var configuredRemote = area == "complete" ? connection.RemotePath : connection.IncompleteRemotePath;
            var configuredLocal = area == "complete" ? connection.LocalPath : connection.IncompleteLocalPath;
            var reported = area == "complete" ? snapshot.ReportedCompletePath : snapshot.ReportedIncompletePath;

            if (connection.DownloadStorageMountId is not { } mountId)
                return (DownloadClientFileSafety.Root(area, configuredRemote, configuredLocal, reported), snapshot, null, null, false);

            var mount = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStorageMountAsync(mountId, token), ct);
            var relativeRoot = DownloadClientFileSafety.StorageRoot(area, configuredRemote, configuredLocal, reported);
            var files = _storage.Open(mount);
            if (files is LocalStorageFiles local)
                return (local.ResolveDirectory(relativeRoot), snapshot, null, null, mount.IsReadOnly);

            // Remote SMB shares accessed directly by Jularr can be browsed safely.
            // Destructive operations stay disabled until atomic remote previews are supported.
            var folder = await files.GetAsync(relativeRoot, ct);
            if (folder is not { IsDirectory: true })
                throw new DataValidationException(nameof(configuredLocal), "invalid_path");
            return (mount.Name + "/" + relativeRoot, snapshot, files, relativeRoot, true);
        }

    }

    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/connections/{acquisitionConnectionId:long}/files/browse")]
    public sealed class BrowseDownloadClientFilesV1 : AdminService<AcquisitionConnectionParametersV1, DownloadClientFolderRequestV1>
    {
        private readonly DownloadClientFilesReader _reader;
        internal BrowseDownloadClientFilesV1(SabnzbdClient client, IDataProtectionProvider protection, StorageAccess storage) => _reader = new DownloadClientFilesReader(client, protection, storage);
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DownloadClientBrowseResultV1>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 p) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 p, Type t) => new("acquisition.manage");
        protected override async Task<IServiceOutput> OnExecute() =>
            await _reader.BrowseAsync(Context, Parameters.AcquisitionConnectionId, Data, CancellationToken);
    }

    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/connections/{acquisitionConnectionId:long}/files/preview")]
    public sealed class PreviewDownloadClientFileDeleteV1 : AdminService<AcquisitionConnectionParametersV1, DownloadClientFolderRequestV1>
    {
        private readonly DownloadClientFilesReader _reader;
        internal PreviewDownloadClientFileDeleteV1(SabnzbdClient client, IDataProtectionProvider protection, StorageAccess storage) => _reader = new DownloadClientFilesReader(client, protection, storage);
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DownloadClientDeletionPreviewV1>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 p) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 p, Type t) => new("acquisition.manage");
        protected override async Task<IServiceOutput> OnExecute() =>
            await _reader.PreviewAsync(Context, Parameters.AcquisitionConnectionId, Data, CancellationToken);
    }

    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/connections/{acquisitionConnectionId:long}/files/delete")]
    public sealed class DeleteDownloadClientFilesV1 : AdminService<AcquisitionConnectionParametersV1, DownloadClientFolderRequestV1>
    {
        private readonly DownloadClientFilesReader _reader;
        internal DeleteDownloadClientFilesV1(SabnzbdClient client, IDataProtectionProvider protection, StorageAccess storage) => _reader = new DownloadClientFilesReader(client, protection, storage);
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 p) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 p, Type t) => new("acquisition.manage");
        protected override async Task<IServiceOutput> OnExecute()
        {
            await _reader.DeleteAsync(Context, Parameters.AcquisitionConnectionId, Data, CancellationToken);
            return new UpdateResult(true);
        }
    }

    internal sealed class DownloadClientFilesReader(SabnzbdClient client, IDataProtectionProvider protection, StorageAccess storage)
        : DownloadClientFilesBase(client, protection, storage)
    {
        private static string RequiredPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new DataValidationException(nameof(path), "required");
            return path;
        }

        internal async Task<DownloadClientBrowseResultV1> BrowseAsync(LogicContext ctx, long id, DownloadClientFolderRequestV1 query, CancellationToken ct)
        {
            var opened = await OpenAsync(ctx, id, query.Area, ct);
            if (opened.NetworkFiles is null)
            {
                var local = DownloadClientFileSafety.Browse(query.Area, opened.Root, query.RelativePath, query.PageNumber, opened.Snapshot);
                if (!opened.ReadOnly) return local;
                return new DownloadClientBrowseResultV1(local.Area, local.Root, local.RelativePath, local.HasMore,
                    new BaseDataList<DownloadClientFileV1>([.. local.Entries.Select(entry =>
                        new DownloadClientFileV1(entry.Name, entry.RelativePath, entry.IsDirectory, entry.SizeBytes, "unchecked"))]));
            }

            if (query.PageNumber is < 1 or > 10000)
                throw new DataValidationException(nameof(query.PageNumber), "invalid_transition");
            var path = DownloadClientFileSafety.StorageRelative(query.RelativePath, allowRoot: true);
            var files = await opened.NetworkFiles.ListAsync(StoragePath.Combine(opened.NetworkRoot!, path), ct);
            var sorted = files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Skip((query.PageNumber - 1) * DownloadClientFileSafety.PageSize)
                .Take(DownloadClientFileSafety.PageSize + 1).ToArray();
            var entries = sorted.Take(DownloadClientFileSafety.PageSize).Select(file =>
                new DownloadClientFileV1(file.Name, StoragePath.Combine(path, file.Name), file.IsDirectory,
                    file.IsDirectory ? null : file.Length, "unchecked")).ToArray();
            return new DownloadClientBrowseResultV1(query.Area, opened.Root + " (read-only)", path,
                sorted.Length > DownloadClientFileSafety.PageSize, new BaseDataList<DownloadClientFileV1>(entries));
        }

        internal async Task<DownloadClientDeletionPreviewV1> PreviewAsync(LogicContext ctx, long id, DownloadClientFolderRequestV1 query, CancellationToken ct)
        {
            var opened = await OpenAsync(ctx, id, query.Area, ct);
            if (opened.ReadOnly || opened.NetworkFiles is not null)
                throw new DataValidationException(nameof(query.Area), "invalid_transition");
            return DownloadClientFileSafety.Preview(query.Area, opened.Root, RequiredPath(query.RelativePath), opened.Snapshot);
        }

        internal async Task DeleteAsync(LogicContext ctx, long id, DownloadClientFolderRequestV1 query, CancellationToken ct)
        {
            if (!query.Confirmed || string.IsNullOrWhiteSpace(query.Fingerprint))
                throw new DataValidationException(nameof(query.Confirmed), "invalid_transition");
            var opened = await OpenAsync(ctx, id, query.Area, ct);
            if (opened.ReadOnly || opened.NetworkFiles is not null)
                throw new DataValidationException(nameof(query.Area), "invalid_transition");
            var checkedPreview = DownloadClientFileSafety.Preview(query.Area, opened.Root, RequiredPath(query.RelativePath), opened.Snapshot);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(checkedPreview.Fingerprint),
                Encoding.ASCII.GetBytes(query.Fingerprint)))
                throw new DataValidationException(nameof(query.Fingerprint), "invalid_transition");
            DownloadClientFileSafety.Delete(opened.Root, RequiredPath(query.RelativePath));
        }
    }
}
