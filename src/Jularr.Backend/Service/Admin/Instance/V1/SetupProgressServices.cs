using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Media;

namespace Jularr.Backend.Service.Admin.Instance.V1
{
    [ServiceEndpoint("PUT", "/api/v1/admin/setup/libraries")]
    public sealed class SaveSetupLibrariesV1 : AdminService<SetupProgressParametersV1, CompleteSetupDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(SetupProgressParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SetupProgressParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.RequireSetupVersionAsync(new SetupVersionData(Data.Version), CancellationToken);
            await Context.DisableUnconfiguredMediaAsync(CancellationToken);
            var modules = await Context.ReadModuleStatesAsync(new InstanceModuleParameters(["Acquisition"]), CancellationToken);
            var next = modules.Any(module => module.IsEnabled) ? SetupStepType.DownloadStorage : SetupStepType.Providers;
            await Context.UpdateSetupStepAsync(new SetupStepData(next), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/setup")]
    public sealed class ReadSetupProgressV1 : AdminService<SetupProgressParametersV1>
    {
        private readonly MediaTranscoder _transcoder;
        private readonly MediaProbeClient _probe;

        internal ReadSetupProgressV1(MediaTranscoder transcoder, MediaProbeClient probe)
        {
            _transcoder = transcoder;
            _probe = probe;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SetupProgressV1>()];
        protected override ModuleRequirement GetInstanceModules(SetupProgressParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SetupProgressParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var state = await Context.ReadSetupStateAsync(CancellationToken);
            var checks = await Context.ReadSetupChecksAsync(CancellationToken);

            // Without FFmpeg files play only as they are.
            var warnings = checks.Where(check => !check.IsBlocking).Select(check => check.CheckKey).ToList();
            if (!_transcoder.IsAvailable || !_probe.IsAvailable)
            {
                warnings.Add("ffmpeg");
            }

            return new SetupProgressV1(state.SetupStepTypeId, state.IsCompleted, state.Version, [.. checks.Where(check => check.IsBlocking).Select(check => check.CheckKey)], [.. warnings]);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/setup")]
    public sealed class UpdateSetupProgressV1 : AdminService<SetupProgressParametersV1, UpdateSetupProgressDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(SetupProgressParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SetupProgressParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateSetupStepAsync(new SetupStepData(Data.SetupStepTypeId), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/setup/completion")]
    public sealed class CompleteSetupV1 : AdminService<SetupProgressParametersV1, CompleteSetupDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(SetupProgressParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SetupProgressParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.CompleteSetupAsync(new SetupVersionData(Data.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
