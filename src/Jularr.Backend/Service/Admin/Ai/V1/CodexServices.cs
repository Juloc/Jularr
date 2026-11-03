using Jularr.Backend.Data.Admin.Ai.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Ai.V1
{
    // The Codex connection of this instance as the CLI reports it now: never the credential files, only the state.
    [ServiceEndpoint("GET", "/api/v1/admin/ai/codex")]
    public sealed class ReadCodexConnectionV1 : AdminService<CodexConnectionParametersV1>
    {
        private readonly CodexConnection _connection;

        internal ReadCodexConnectionV1(CodexConnection connection)
        {
            _connection = connection;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CodexConnectionV1>()];
        protected override ModuleRequirement GetInstanceModules(CodexConnectionParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(CodexConnectionParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead() => await _connection.ReadAsync(CancellationToken);
    }

    // Starts the device login: the answer carries the address and the one-time code for the person.
    [ServiceEndpoint("POST", "/api/v1/admin/ai/codex/login")]
    public sealed class StartCodexLoginV1 : AdminService<CodexConnectionParametersV1, NoData>
    {
        private readonly CodexConnection _connection;

        internal StartCodexLoginV1(CodexConnection connection)
        {
            _connection = connection;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CodexConnectionV1>()];
        protected override ModuleRequirement GetInstanceModules(CodexConnectionParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(CodexConnectionParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnExecute() => await _connection.StartLoginAsync(CancellationToken);
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/ai/codex/login")]
    public sealed class CancelCodexLoginV1 : AdminService<CodexConnectionParametersV1, NoData>
    {
        private readonly CodexConnection _connection;

        internal CancelCodexLoginV1(CodexConnection connection)
        {
            _connection = connection;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CodexConnectionParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(CodexConnectionParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override Task<IServiceOutput> OnDelete()
        {
            _connection.CancelLogin();
            return Task.FromResult<IServiceOutput>(new UpdateResult(true));
        }
    }

    // Removes the stored sign-in with the CLI.
    [ServiceEndpoint("DELETE", "/api/v1/admin/ai/codex")]
    public sealed class LogoutCodexV1 : AdminService<CodexConnectionParametersV1, NoData>
    {
        private readonly CodexConnection _connection;

        internal LogoutCodexV1(CodexConnection connection)
        {
            _connection = connection;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CodexConnectionParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(CodexConnectionParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await _connection.LogoutAsync(CancellationToken);
            return new UpdateResult(true);
        }
    }
}
