using Jularr.Backend.Data.Admin.Speech.V1;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Speech;
using Jularr.Backend.Data.User.Speech.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Speech;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Speech.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/speech/models")]
    public sealed class ReadSpeechModelsV1 : AdminService<SearchSpeechModelsParametersV1>
    {
        private readonly SpeechFiles _files;
        internal ReadSpeechModelsV1(SpeechFiles files) { _files = files; }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SpeechModelCatalogV1>()];
        protected override PageRequest GetPageRequest(SearchSpeechModelsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);
        protected override ModuleRequirement GetInstanceModules(SearchSpeechModelsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SearchSpeechModelsParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var all = await _files.CatalogAsync(CancellationToken);
            var catalog = all.Where(model =>
                (Parameters.Query is null || model.Name.Contains(Parameters.Query, StringComparison.OrdinalIgnoreCase) || model.Id.Contains(Parameters.Query, StringComparison.OrdinalIgnoreCase)
                    || model.Language.Contains(Parameters.Query, StringComparison.OrdinalIgnoreCase))
                && (Parameters.Language is null || model.Language.Equals(Parameters.Language, StringComparison.OrdinalIgnoreCase))
                && (Parameters.Gender is null || model.Gender == Parameters.Gender))
                .Select(model => (Model: model, Installed: IsInstalled(model)))
                .Where(item => Parameters.IsInstalled is null || item.Installed == Parameters.IsInstalled)
                .Skip((int)Page.Offset).Take(Page.PageSize).ToArray();
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    model."Hash",
                    model."IsEnabled",
                    model."Version"
                FROM
                    "SpeechModels" AS model
                WHERE
                    model."Hash" = ANY(@Hashes);
                """;
            sql.Parameters.Add(new SpeechModelHashesData([.. catalog.Select(item => item.Model.Sha256)]));
            var states = (await Context.ReadSql.ReadListAsync<SpeechModelData>(sql, CancellationToken)).ToDictionary(item => item.Hash, StringComparer.Ordinal);
            var models = new PageResult<SpeechModelV1>(catalog.Select(item =>
            {
                var model = item.Model;
                states.TryGetValue(model.Sha256, out var state);
                return new SpeechModelV1(model.Id, model.Version, model.Name, model.Language, model.Voice, model.License, model.LicenseUrl.AbsoluteUri,
                    model.Source.AbsoluteUri, model.SizeBytes, model.Sha256, state?.IsEnabled == true && item.Installed,
                    state?.Version ?? 0, model.RequiresLicenseAcceptance, model.SampleUrl?.AbsoluteUri, model.Gender, model.GenderSource?.AbsoluteUri, item.Installed);
            }).ToArray(), Page);
            return new SpeechModelCatalogV1(models, all.Select(model => model.Language).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        private bool IsInstalled(SpeechModel model)
        {
            var file = new FileInfo(_files.PathFor(model.Sha256));
            return file.Exists && file.Length == model.SizeBytes;
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/speech/models/{hash}/activation")]
    public sealed class UpdateSpeechModelV1 : AdminService<SpeechModelParametersV1, UpdateSpeechModelDataV1>
    {
        private readonly SpeechFiles _files;
        internal UpdateSpeechModelV1(SpeechFiles files) { _files = files; }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(SpeechModelParametersV1 parameters) => ModuleRequirement.All("Reading");
        protected override ServicePermission GetPermission(SpeechModelParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var catalog = await _files.CatalogAsync(CancellationToken);
            var model = catalog.FirstOrDefault(item => item.Sha256 == Parameters.Hash) ?? throw new KeyNotFoundException("Unknown speech model.");
            if (Data.IsEnabled && model.RequiresLicenseAcceptance && !Data.AcceptLicense)
            {
                throw new DataValidationException(nameof(Data.AcceptLicense), "speech_license_confirmation");
            }
            if (Data.IsEnabled) await _files.InstallAsync(model, CancellationToken);

            // Recheck permissions and module state after I/O; no transaction spans a download.
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.UpdateSpeechModelAsync(new SpeechModelData(Parameters.Hash, Data.IsEnabled, Data.Version), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/speech/models/refresh")]
    public sealed class RefreshSpeechModelsV1 : AdminService<SpeechModelsParametersV1, NoData>
    {
        private readonly SpeechFiles _files;
        internal RefreshSpeechModelsV1(SpeechFiles files) { _files = files; }
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(SpeechModelsParametersV1 parameters) => ModuleRequirement.All("Reading");
        protected override ServicePermission GetPermission(SpeechModelsParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var discovered = await _files.DiscoverAsync(CancellationToken);
            // Revalidate after external I/O; only a small atomic metadata write remains.
            await Context.ExecuteBatchAsync(async (_, ct) =>
            {
                await _files.SaveDiscoveredAsync(discovered, ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
