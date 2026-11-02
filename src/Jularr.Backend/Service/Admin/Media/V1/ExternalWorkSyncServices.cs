using System.Text.Json;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/catalog/syncs")]
    public sealed class ReadExternalWorkSyncsV1 : AdminService<ReadExternalWorkSyncsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<ExternalWorkSyncV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadExternalWorkSyncsParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadExternalWorkSyncsParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var syncs = await Context.ReadExternalWorkSyncSummariesAsync(CancellationToken);
            var windows = await Context.ReadExternalWorkSyncWindowsAsync(CancellationToken);
            var tier = await Context.ReadCachingTierAsync(CancellationToken);
            return new BaseDataList<ExternalWorkSyncV1>([.. syncs.Select(sync => new ExternalWorkSyncV1(sync.Namespace, ExternalWorkLogic.CatalogTier(tier, sync.Namespace), sync.RequestsPerMinute,
                sync.PausesWhileUsed, sync.ListedOn, sync.Entries, sync.Pending, sync.Version,
                new BaseDataList<ExternalWorkSyncWindowV1>([.. windows.Where(window => window.Namespace == sync.Namespace).Select(window => new ExternalWorkSyncWindowV1(window.StartsAt, window.EndsAt))])))]);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/catalog/syncs/{namespace}")]
    public sealed class UpdateExternalWorkSyncV1 : AdminService<ExternalWorkSyncParametersV1, UpdateExternalWorkSyncDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ExternalWorkSyncParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ExternalWorkSyncParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // A window needs an end other than its start.
            if (Data.Windows.Any(window => window.StartsAt == window.EndsAt))
            {
                throw new DataValidationException(nameof(UpdateExternalWorkSyncDataV1.Windows), "invalid_value");
            }

            var windows = JsonSerializer.Serialize(Data.Windows.Select(window => new { StartsAt = window.StartsAt.ToString("HH:mm"), EndsAt = window.EndsAt.ToString("HH:mm") }));
            await Context.UpdateExternalWorkSyncSettingsAsync(new ExternalWorkSyncSettingsData(Parameters.Namespace, Data.RequestsPerMinute, Data.PausesWhileUsed,
                Data.Version, windows), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
