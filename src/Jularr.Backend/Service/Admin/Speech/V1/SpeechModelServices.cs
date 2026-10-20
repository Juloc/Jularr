using Jularr.Backend.Data.Admin.Speech.V1;
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
    public sealed class ReadSpeechModelsV1 : AdminService<SpeechModelsParametersV1>
    {
        private readonly SpeechFiles _files;
        internal ReadSpeechModelsV1(SpeechFiles files) { _files = files; }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<SpeechModelV1>>()];
        protected override ModuleRequirement GetInstanceModules(SpeechModelsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(SpeechModelsParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var catalog = await _files.CatalogAsync(CancellationToken);
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
            sql.Parameters.Add(new SpeechModelHashesData([.. catalog.Select(model => model.Sha256)]));
            var states = (await Context.ReadSql.ReadListAsync<SpeechModelData>(sql, CancellationToken)).ToDictionary(item => item.Hash, StringComparer.Ordinal);
            return new BaseDataList<SpeechModelV1>(catalog.Select(model =>
            {
                states.TryGetValue(model.Sha256, out var state);
                var path = _files.PathFor(model.Sha256);
                return new SpeechModelV1(model.Id, model.Version, model.Name, model.Language, model.Voice, model.License, model.LicenseUrl.AbsoluteUri,
                    model.Source.AbsoluteUri, model.SizeBytes, model.Sha256, state?.IsEnabled == true && File.Exists(path) && new FileInfo(path).Length == model.SizeBytes, state?.Version ?? 0);
            }));
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
}
