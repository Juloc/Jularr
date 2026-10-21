using Jularr.Backend.Data.Admin.Transcoding.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Playback.V1;
using Jularr.Integrations.Logic.Media;
using Microsoft.Extensions.Options;
using OperationKind = Jularr.Backend.Data.Enums.Operation.OperationType;

namespace Jularr.Backend.Service.Admin.Transcoding.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/transcoding/settings")]
    public sealed class ReadTranscodeSettingsV1 : AdminService<TranscodeSettingsParametersV1>
    {
        private readonly MediaOptions _options;

        internal ReadTranscodeSettingsV1(IOptions<MediaOptions> options)
        {
            _options = options.Value;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<TranscodeSettingsV1>()];
        protected override ModuleRequirement GetInstanceModules(TranscodeSettingsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(TranscodeSettingsParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Without a saved maximum the configuration's counts.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."TranscodeAccelerationTypeId",
                    settings."MaxTranscodes"::integer AS "MaxTranscodes",
                    settings."LadderHeights",
                    settings."Version"
                FROM
                    "TranscodeSettings" AS settings
                WHERE
                    settings."Id" = 1;
                """;
            var settings = await Context.ReadSql.ReadRequiredAsync<TranscodeSettingsData>(sql, CancellationToken);
            return new TranscodeSettingsV1(settings.TranscodeAccelerationTypeId, settings.MaxTranscodes ?? _options.MaxTranscodes, settings.LadderHeights, settings.Version);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/transcoding/settings")]
    public sealed class UpdateTranscodeSettingsV1 : AdminService<TranscodeSettingsParametersV1, UpdateTranscodeSettingsDataV1>
    {
        private readonly PlaybackTranscodes _transcodes;

        internal UpdateTranscodeSettingsV1(PlaybackTranscodes transcodes)
        {
            _transcodes = transcodes;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(TranscodeSettingsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(TranscodeSettingsParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateTranscodeSettingsAsync(new TranscodeSettingsUpdateData(Data.TranscodeAccelerationTypeId, Data.MaxTranscodes, Data.LadderHeights, Data.Version), CancellationToken);
            return new UpdateResult(true);
        }

        // The new maximum counts for conversions that restart, not only for new plans.
        protected override void OnCommitted() => _transcodes.MaxTranscodes = Data.MaxTranscodes;
    }

    [ServiceEndpoint("GET", "/api/v1/admin/transcoding/benchmark")]
    public sealed class ReadTranscodeBenchmarkV1 : AdminService<TranscodeBenchmarkParametersV1>
    {
        private readonly TranscodeHardware _hardware;

        internal ReadTranscodeBenchmarkV1(TranscodeHardware hardware)
        {
            _hardware = hardware;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<TranscodeBenchmarkV1>()];
        protected override ModuleRequirement GetInstanceModules(TranscodeBenchmarkParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(TranscodeBenchmarkParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The last result with the latest run's state.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    benchmark."RanAt",
                    benchmark."CpuCores"::integer AS "CpuCores",
                    benchmark."BestTranscodeAccelerationTypeId",
                    benchmark."RecommendedMaxTranscodes"::integer AS "RecommendedMaxTranscodes",
                    coalesce(latest."OperationStatusTypeId" IN (1, 2), FALSE) AS "IsRunning",
                    CASE WHEN latest."OperationStatusTypeId" = 4 THEN latest."ErrorKey" END AS "ErrorKey"
                FROM
                    (SELECT 1 AS "Id") AS one
                    LEFT JOIN "TranscodeBenchmarks" AS benchmark ON benchmark."Id" = one."Id"
                    LEFT JOIN LATERAL (
                        SELECT
                            operation."OperationStatusTypeId",
                            operation."ErrorKey"
                        FROM
                            "Operations" AS operation
                        WHERE
                            operation."OperationTypeId" = 4
                        ORDER BY
                            operation."CreatedAt" DESC,
                            operation."Id" DESC
                        LIMIT 1
                    ) AS latest ON TRUE;
                """;
            var benchmark = await Context.ReadSql.ReadRequiredAsync<TranscodeBenchmarkRowData>(sql, CancellationToken);

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    result."TranscodeAccelerationTypeId",
                    result."Height",
                    result."SpeedFactor"
                FROM
                    "TranscodeBenchmarkResults" AS result
                ORDER BY
                    result."TranscodeAccelerationTypeId",
                    result."Height";
                """;
            var results = await Context.ReadSql.ReadListAsync<TranscodeBenchmarkResultV1>(sql, CancellationToken);
            var hardware = (await _hardware.GetAsync(CancellationToken)).Select(item => new TranscodeHardwareV1(TranscodeLogic.Acceleration(item.Pipeline), item.Encoder, item.Device, item.IsAvailable));
            return new TranscodeBenchmarkV1(benchmark.RanAt, benchmark.CpuCores, benchmark.BestTranscodeAccelerationTypeId, benchmark.RecommendedMaxTranscodes, benchmark.IsRunning, benchmark.ErrorKey,
                new BaseDataList<TranscodeHardwareV1>(hardware), new BaseDataList<TranscodeBenchmarkResultV1>(results));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/transcoding/benchmark")]
    public sealed class QueueTranscodeBenchmarkV1 : AdminService<TranscodeBenchmarkParametersV1, NoData>
    {
        private readonly TranscodeBenchmarkClient _client;

        internal QueueTranscodeBenchmarkV1(TranscodeBenchmarkClient client)
        {
            _client = client;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(TranscodeBenchmarkParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(TranscodeBenchmarkParametersV1 parameters, Type resultType) => new("instance.settings.update");

        // Never at startup: the benchmark runs when an administrator asks, in the background.
        protected override async Task<IServiceOutput> OnCreate()
        {
            if (!_client.IsAvailable)
            {
                throw new ServiceUnavailableException("benchmark_unavailable");
            }

            return new UpdateResult(await Context.QueueTranscodeBenchmarkAsync(new OperationCreationData(OperationKind.TranscodeBenchmark, null, Auth.AccountId), CancellationToken));
        }
    }
}
