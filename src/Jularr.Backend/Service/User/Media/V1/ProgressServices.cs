using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Media.V1
{
    // A profile marks a title, a season, an episode, a volume or a track watched or unwatched; only its own progress changes.
    [ServiceEndpoint("PUT", "/api/v1/works/{workId:long}/progress")]
    public sealed class UpdateWorkProgressV1 : UserService<WorkParametersV1, UpdateWorkProgressDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var change = new ProgressChangeData(Auth.ProfileId!.Value, Parameters.WorkId, Data.WorkUnitId);
            if (Data.Action == ProgressAction.Watched)
            {
                await Context.MarkWatchedAsync(change, CancellationToken);
            }
            else
            {
                await Context.MarkUnwatchedAsync(change, CancellationToken);
            }

            return new UpdateResult(true);
        }
    }
}
