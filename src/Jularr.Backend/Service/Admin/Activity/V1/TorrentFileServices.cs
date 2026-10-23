using System.Text.Json;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Integrations.Data.Qbittorrent;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Qbittorrent;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Activity.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/activity/{operationId:long}/files")]
    public sealed class ReadTorrentFilesV1 : AdminService<ActivityOperationParametersV1>
    {
        private readonly QbittorrentClient _client;
        private readonly IDataProtector _protector;

        internal ReadTorrentFilesV1(QbittorrentClient client, IDataProtectionProvider protection)
        {
            _client = client;
            _protector = protection.CreateProtector(AcquisitionConnectionInput.ProtectorPurpose);
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<TorrentFileV1>>()];
        protected override ModuleRequirement GetInstanceModules(ActivityOperationParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ActivityOperationParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnRead()
        {
            var download = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadDownloadActionAsync(Parameters.OperationId, ct), CancellationToken);
            if (download is null || download.AcquisitionConnectionTypeId != AcquisitionConnectionType.Qbittorrent || download.ExternalId is null)
                return new BaseDataList<TorrentFileV1>([]);

            var connection = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadAcquisitionConnectionAsync(download.AcquisitionConnectionId, ct), CancellationToken);
            try
            {
                var files = await _client.GetFilesAsync(DownloadActions.TorrentConnection(connection, _protector), download.ExternalId, CancellationToken);
                return new BaseDataList<TorrentFileV1>([.. files.Select(file =>
                    new TorrentFileV1(file.Name, file.SizeBytes, (int)Math.Round(file.Progress * 100), file.IsSelected))]);
            }
            catch (Exception exception) when (exception is HttpRequestException or QbittorrentException or JsonException
                || exception is TaskCanceledException && !CancellationToken.IsCancellationRequested)
            {
                throw new ServiceUnavailableException("acquisition_unavailable");
            }
        }
    }
}
