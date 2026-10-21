using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // The episodes of a title with the markers of their first file and where each one comes from.
    [ServiceEndpoint("GET", "/api/v1/admin/works/{workId:long}/markers")]
    public sealed class ReadWorkMarkersV1 : AdminService<WorkParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<WorkMarkerRowV1>>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH episode AS (
                    SELECT
                        unit."Id" AS "WorkUnitId",
                        coalesce(season."Number", 0)::integer AS "SeasonNumber",
                        unit."Number",
                        unit."Title",
                        (
                            SELECT
                                file."Id"
                            FROM
                                "MediaAssets" AS asset
                                JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                            WHERE
                                asset."WorkUnitId" = unit."Id"
                                AND file."MissingSince" IS NULL
                                AND NOT file."IsSubtitle"
                            ORDER BY
                                asset."Id",
                                file."Id"
                            LIMIT 1
                        ) AS "StoredFileId"
                    FROM
                        "WorkUnits" AS unit
                        LEFT JOIN "WorkUnits" AS season ON season."Id" = unit."ParentWorkUnitId"
                    WHERE
                        unit."WorkId" = @WorkId
                        AND unit."WorkUnitTypeId" = 2
                )
                SELECT
                    episode."WorkUnitId",
                    episode."SeasonNumber",
                    episode."Number",
                    episode."Title",
                    episode."StoredFileId",
                    file."DurationMs",
                    (SELECT count(*) FROM "MediaChapters" AS chapter WHERE chapter."StoredFileId" = episode."StoredFileId")::integer AS "ChapterCount",
                    marker."MediaMarkerKindTypeId",
                    marker."MediaMarkerSourceTypeId",
                    marker."StartMs",
                    marker."EndMs"
                FROM
                    episode
                    LEFT JOIN "StoredFiles" AS file ON file."Id" = episode."StoredFileId"
                    LEFT JOIN LATERAL (
                        SELECT DISTINCT ON (found."MediaMarkerKindTypeId")
                            found."MediaMarkerKindTypeId",
                            found."MediaMarkerSourceTypeId",
                            found."StartMs",
                            found."EndMs"
                        FROM
                            "MediaMarkers" AS found
                        WHERE
                            found."StoredFileId" = episode."StoredFileId"
                        ORDER BY
                            found."MediaMarkerKindTypeId",
                            found."MediaMarkerSourceTypeId"
                    ) AS marker ON TRUE
                ORDER BY
                    episode."SeasonNumber",
                    episode."Number",
                    marker."MediaMarkerKindTypeId";
                """;
            sql.Parameters.Add(Parameters);
            return new BaseDataList<WorkMarkerRowV1>(await Context.ReadSql.ReadListAsync<WorkMarkerRowV1>(sql, CancellationToken));
        }
    }

    // A marker set by hand on every available file of the episode.
    [ServiceEndpoint("PUT", "/api/v1/admin/works/{workId:long}/units/{workUnitId:long}/markers")]
    public sealed class SaveWorkMarkerV1 : AdminService<WorkUnitMarkerParametersV1, SaveWorkMarkerDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkUnitMarkerParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(WorkUnitMarkerParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var start = (long)Math.Round(Data.StartSeconds * 1000m);
            var end = (long)Math.Round(Data.EndSeconds * 1000m);
            await Context.SaveManualMarkerAsync(new MediaMarkerSaveData(Parameters.WorkId, Parameters.WorkUnitId, Data.MediaMarkerKindTypeId, start, end), CancellationToken);
            return new UpdateResult(true);
        }
    }

    // The manual marker of one kind goes; what the file or a sidecar says shows again.
    [ServiceEndpoint("DELETE", "/api/v1/admin/works/{workId:long}/units/{workUnitId:long}/markers/{mediaMarkerKindTypeId}")]
    public sealed class DeleteWorkMarkerV1 : AdminService<WorkUnitMarkerKindParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkUnitMarkerKindParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(WorkUnitMarkerKindParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.DeleteManualMarkerAsync(new MediaMarkerKeyData(Parameters.WorkId, Parameters.WorkUnitId, Parameters.MediaMarkerKindTypeId), CancellationToken);
            return new UpdateResult(true);
        }
    }

    // The episode's marker of one kind goes to the other episodes of its season, shifted by the offset.
    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/units/{workUnitId:long}/markers/copy")]
    public sealed class CopyWorkMarkersV1 : AdminService<WorkUnitMarkerParametersV1, CopyWorkMarkersDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CopyWorkMarkersResultV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkUnitMarkerParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(WorkUnitMarkerParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var offset = (long)Math.Round(Data.OffsetSeconds * 1000m);
            var data = new MediaMarkerCopyData(Parameters.WorkId, Parameters.WorkUnitId, Data.MediaMarkerKindTypeId, offset);
            var copied = await Context.ExecuteBatchAsync((ctx, ct) => ctx.CopyMarkerToSeasonAsync(data, ct), CancellationToken);
            return new CopyWorkMarkersResultV1(copied);
        }
    }
}
