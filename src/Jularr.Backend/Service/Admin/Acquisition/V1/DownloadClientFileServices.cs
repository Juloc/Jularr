using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Sabnzbd;
using Jularr.Integrations.Logic.Sabnzbd;
using Jularr.Integrations.Logic.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    internal abstract class DownloadClientFilesBase(SabnzbdClient client, IDataProtectionProvider protection, StorageAccess storage)
    {
        protected readonly SabnzbdClient Client = client;
        private readonly StorageAccess _storage = storage;
        private readonly IDataProtector _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);

        protected static void CheckArea(string area)
        {
            if (area is not ("complete" or "incomplete"))
            {
                throw new DataValidationException(nameof(area), "invalid_transition");
            }
        }

        protected async Task<(string Root, SabnzbdConsoleSnapshot Snapshot, StorageFiles? NetworkFiles, string? NetworkRoot, bool ReadOnly)> OpenAsync(
            LogicContext context, long connectionId, string area, CancellationToken ct)
        {
            CheckArea(area);
            var connection = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadAcquisitionConnectionAsync(connectionId, token), ct);
            if (connection.AcquisitionConnectionTypeId != AcquisitionConnectionType.Sabnzbd)
            {
                throw new DataValidationException(nameof(connectionId), "invalid_transition");
            }

            var api = new SabnzbdConnection(connection.HttpAddress, AcquisitionConnectionInput.Unprotect(_protector, connection.ProtectedApiKey));
            var snapshot = await Client.ReadConsoleAsync(api, ct);
            var configuredRemote = area == "complete" ? connection.RemotePath : connection.IncompleteRemotePath;
            var configuredLocal = area == "complete" ? connection.LocalPath : connection.IncompleteLocalPath;
            var reported = area == "complete" ? snapshot.ReportedCompletePath : snapshot.ReportedIncompletePath;

            if (connection.DownloadStorageMountId is not { } mountId)
            {
                return (DownloadClientFileSafety.Root(area, configuredRemote, configuredLocal, reported), snapshot, null, null, false);
            }

            var mount = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadStorageMountAsync(mountId, token), ct);
            var relativeRoot = DownloadClientFileSafety.StorageRoot(area, configuredRemote, configuredLocal, reported);
            var files = _storage.Open(mount);
            if (files is LocalStorageFiles local)
            {
                return (local.ResolveDirectory(relativeRoot), snapshot, null, null, mount.IsReadOnly);
            }

            // Remote SMB shares accessed directly by Jularr can be browsed safely.
            // Destructive operations stay disabled until atomic remote previews are supported.
            var folder = await files.GetAsync(relativeRoot, ct);
            if (folder is not { IsDirectory: true })
            {
                throw new DataValidationException(nameof(configuredLocal), "invalid_path");
            }
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
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");
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
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");
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
        protected override ModuleRequirement GetInstanceModules(AcquisitionConnectionParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionConnectionParametersV1 parameters, Type resultType) => new("acquisition.manage");
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
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new DataValidationException(nameof(path), "required");
            }
            return path;
        }

        internal async Task<DownloadClientBrowseResultV1> BrowseAsync(LogicContext ctx, long id, DownloadClientFolderRequestV1 query, CancellationToken ct)
        {
            var opened = await OpenAsync(ctx, id, query.Area, ct);
            if (opened.NetworkFiles is null)
            {
                var local = DownloadClientFileSafety.Browse(query.Area, opened.Root, query.RelativePath, query.PageNumber, opened.Snapshot);
                if (!opened.ReadOnly)
                {
                    return local;
                }
                return new DownloadClientBrowseResultV1(local.Area, local.Root, local.RelativePath, local.HasMore,
                    new BaseDataList<DownloadClientFileV1>([.. local.Entries.Select(entry =>
                        new DownloadClientFileV1(entry.Name, entry.RelativePath, entry.IsDirectory, entry.SizeBytes, "unchecked"))]));
            }

            if (query.PageNumber is < 1 or > 10000)
            {
                throw new DataValidationException(nameof(query.PageNumber), "invalid_transition");
            }
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
            {
                throw new DataValidationException(nameof(query.Area), "invalid_transition");
            }
            return DownloadClientFileSafety.Preview(query.Area, opened.Root, RequiredPath(query.RelativePath), opened.Snapshot);
        }

        internal async Task DeleteAsync(LogicContext ctx, long id, DownloadClientFolderRequestV1 query, CancellationToken ct)
        {
            if (!query.Confirmed || string.IsNullOrWhiteSpace(query.Fingerprint))
            {
                throw new DataValidationException(nameof(query.Confirmed), "invalid_transition");
            }
            var opened = await OpenAsync(ctx, id, query.Area, ct);
            if (opened.ReadOnly || opened.NetworkFiles is not null)
            {
                throw new DataValidationException(nameof(query.Area), "invalid_transition");
            }
            var checkedPreview = DownloadClientFileSafety.Preview(query.Area, opened.Root, RequiredPath(query.RelativePath), opened.Snapshot);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(checkedPreview.Fingerprint),
                Encoding.ASCII.GetBytes(query.Fingerprint)))
            {
                throw new DataValidationException(nameof(query.Fingerprint), "invalid_transition");
            }
            DownloadClientFileSafety.Delete(opened.Root, RequiredPath(query.RelativePath));
        }
    }
}
