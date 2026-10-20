using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Speech;
using Jularr.Backend.Data.User.Speech.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Speech.V1
{
    [ServiceEndpoint("GET", "/api/v1/speech/models")]
    public sealed class ReadAvailableSpeechModelsV1 : UserService<SpeechModelsParametersV1>
    {
        private readonly SpeechFiles _files;
        internal ReadAvailableSpeechModelsV1(SpeechFiles files) { _files = files; }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<SpeechModelV1>>()];
        protected override ModuleRequirement GetInstanceModules(SpeechModelsParametersV1 parameters) => ModuleRequirement.All("Reading");
        protected override ServicePermission GetPermission(SpeechModelsParametersV1 parameters, Type resultType) => new("profile.media.read");

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
                    model."IsEnabled" = TRUE
                    AND model."Hash" = ANY(@Hashes);
                """;
            sql.Parameters.Add(new SpeechModelHashesData([.. catalog.Select(model => model.Sha256)]));
            var states = (await Context.ReadSql.ReadListAsync<SpeechModelData>(sql, CancellationToken)).ToDictionary(item => item.Hash, StringComparer.Ordinal);
            return new BaseDataList<SpeechModelV1>(catalog.Where(model => states.ContainsKey(model.Sha256) && File.Exists(_files.PathFor(model.Sha256))
                && new FileInfo(_files.PathFor(model.Sha256)).Length == model.SizeBytes)
                .Select(model => new SpeechModelV1(model.Id, model.Version, model.Name, model.Language, model.Voice, model.License, model.LicenseUrl.AbsoluteUri,
                    model.Source.AbsoluteUri, model.SizeBytes, model.Sha256, true, states[model.Sha256].Version)));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/speech/models/{hash}/file")]
    public sealed class ReadSpeechModelFileV1 : UserService<SpeechModelParametersV1>
    {
        private readonly SpeechFiles _files;
        internal ReadSpeechModelFileV1(SpeechFiles files) { _files = files; }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SpeechModelFileV1>()];
        protected override ModuleRequirement GetInstanceModules(SpeechModelParametersV1 parameters) => ModuleRequirement.All("Reading");
        protected override ServicePermission GetPermission(SpeechModelParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var model = (await _files.CatalogAsync(CancellationToken)).FirstOrDefault(item => item.Sha256 == Parameters.Hash)
                ?? throw new KeyNotFoundException("Unknown speech model.");
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
                    model."Hash" = @Hash
                    AND model."IsEnabled" = TRUE;
                """;
            sql.Parameters.Add(Parameters);
            _ = await Context.ReadOptionalAsync<SpeechModelData>(sql, CancellationToken) ?? throw new KeyNotFoundException("The speech model is not enabled.");
            var path = _files.PathFor(model.Sha256);
            if (!File.Exists(path) || new FileInfo(path).Length != model.SizeBytes) throw new ServiceUnavailableException("speech_download_failed");
            return new SpeechModelFileV1(File.OpenRead(path));
        }
    }
}
