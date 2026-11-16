using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // All files of an episode, a volume or a track, or of a whole season, go in one step and the unit (every episode of a season) is not monitored any more,
    // so the next search does not fetch them again. The unit itself stays in the Work. Files that cannot be removed stay and are counted.
    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/units/{workUnitId:long}/files/deletion")]
    public sealed class DeleteWorkUnitFilesV1 : AdminService<WorkUnitParametersV1, NoData>
    {
        // The files of one pass: a pass may name 500 files at most.
        private const int Pass = 500;

        private readonly LibraryReplacements _replacements;

        internal DeleteWorkUnitFilesV1(LibraryReplacements replacements)
        {
            _replacements = replacements;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DeleteLibraryFilesResultV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkUnitParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkUnitParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var query = new WorkUnitFilesQueryData(Parameters.WorkId, Parameters.WorkUnitId);
            var (target, files) = await Context.ExecuteBatchAsync(async (ctx, token) => (await ctx.ReadWorkUnitTargetAsync(query, token), await ctx.ReadWorkUnitFilesAsync(query, token)), CancellationToken);
            var ids = files.Select(file => file.StoredFileId).ToArray();
            var deleted = 0;
            var kept = 0;
            for (var start = 0; start < ids.Length; start += Pass)
            {
                var (passDeleted, passKept) = await _replacements.DeleteAsync(Context, ids[start..Math.Min(ids.Length, start + Pass)], Auth.AccountId!.Value, CancellationToken);
                deleted += passDeleted;
                kept += passKept;
            }

            // A season switches all its episodes; a track has no switch. Only when something went: nothing deleted changes nothing else.
            if (deleted > 0 && target.WantedItemId is { } wantedItemId && target.WorkUnitTypeId is WorkUnitType.Season or WorkUnitType.Episode or WorkUnitType.Volume)
            {
                await Context.SetUnitMonitoringAsync(new WantedUnitMonitoringData(wantedItemId, Parameters.WorkUnitId, false), CancellationToken);
            }

            return new DeleteLibraryFilesResultV1(deleted, kept);
        }
    }
}
