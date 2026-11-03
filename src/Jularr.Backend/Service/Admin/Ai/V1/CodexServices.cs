using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Ai.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Infrastructure.Sql;
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

    // The models and plan usage of the signed-in account; the page offers its choices from this.
    [ServiceEndpoint("GET", "/api/v1/admin/ai/codex/catalog")]
    public sealed class ReadCodexCatalogV1 : AdminService<CodexConnectionParametersV1>
    {
        private readonly CodexConnection _connection;

        internal ReadCodexCatalogV1(CodexConnection connection)
        {
            _connection = connection;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CodexCatalogV1>()];
        protected override ModuleRequirement GetInstanceModules(CodexConnectionParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(CodexConnectionParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var catalog = await _connection.ReadCatalogAsync(CancellationToken);
            var (today, sinceStart) = _connection.Usage();
            return catalog is null ? new CodexCatalogV1(false, [], null, [], today, sinceStart)
                : new CodexCatalogV1(true, [.. catalog.Models.Select(model => new CodexModelV1(model.Id, model.Name, model.Efforts, model.DefaultEffort, model.IsDefault))], catalog.Usage?.Plan,
                    [.. (catalog.Usage?.Windows ?? []).Select(window => new CodexUsageWindowV1(window.UsedPercent, window.Minutes, window.ResetsAt))], today, sinceStart);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/ai/codex/settings")]
    public sealed class ReadCodexSettingsV1 : AdminService<CodexConnectionParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CodexSettingsV1>()];
        protected override ModuleRequirement GetInstanceModules(CodexConnectionParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(CodexConnectionParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead() => await CodexSettings.ReadAsync(Context, CancellationToken);
    }

    // The Owner's model, effort, fallback and token limits. A fallback equal to the model is the same as none.
    [ServiceEndpoint("PUT", "/api/v1/admin/ai/codex/settings")]
    public sealed class SaveCodexSettingsV1 : AdminService<CodexConnectionParametersV1, CodexSettingsDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CodexConnectionParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(CodexConnectionParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var model = Blank(Data.Model);
            var fallback = Blank(Data.FallbackModel);
            if (fallback is not null && string.Equals(fallback, model, StringComparison.Ordinal)) fallback = null;
            var effort = Blank(Data.Effort);
            if (model is not null && !CodexSettings.IsModelName(model)) throw new DataValidationException(nameof(Data.Model), "codex_model_invalid");
            if (fallback is not null && !CodexSettings.IsModelName(fallback)) throw new DataValidationException(nameof(Data.FallbackModel), "codex_model_invalid");
            if (effort is not null && !CodexSettings.IsEffortName(effort)) throw new DataValidationException(nameof(Data.Effort), "codex_effort_invalid");
            if (Data.DailyTokens > 0 && Data.RunTokens > Data.DailyTokens) throw new DataValidationException(nameof(Data.RunTokens), "codex_run_over_daily");

            var sql = new SqlStatement();
            sql.Statement = """
                UPDATE "InstanceSettings" SET "CodexModel" = @Model, "CodexEffort" = @Effort, "CodexFallbackModel" = @FallbackModel, "CodexDailyTokens" = @DailyTokens,
                    "CodexRunTokens" = @RunTokens, "CodexWarnPercent" = @WarnPercent WHERE "Id" = 1;
                """;
            sql.Parameters.Add(new CodexSettingsDataV1(model, effort, fallback, Data.DailyTokens, Data.RunTokens, Data.WarnPercent));
            await Context.ExecuteAsyncForAffectedRows(sql, CancellationToken);
            return new UpdateResult(true);
        }

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
