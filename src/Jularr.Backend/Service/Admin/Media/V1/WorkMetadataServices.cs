using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/metadata/search")]
    public sealed class SearchMetadataV1 : AdminService<SearchMetadataParametersV1, SearchMetadataDataV1>
    {
        private readonly WorkMetadata _metadata;

        internal SearchMetadataV1(WorkMetadata metadata)
        {
            _metadata = metadata;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<MetadataSearchResultV1>>()];
        protected override ModuleRequirement GetInstanceModules(SearchMetadataParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(SearchMetadataParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var connection = await _metadata.ConnectAsync(Context, CancellationToken);
            var results = await _metadata.SearchAsync(connection, Data.MediaTypeId, Data.Query, Data.ReleaseYear, CancellationToken);

            // Mark results that already are Works.
            var known = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        identity."Namespace",
                        identity."Value",
                        identity."WorkId"
                    FROM
                        "WorkExternalIdentities" AS identity
                    WHERE
                        identity."Namespace" = @Namespace
                        AND identity."Value" IN (SELECT jsonb_array_elements_text(@Values::jsonb));
                    """;
                sql.Parameters.Add(new WorkIdentityLookupData(WorkMetadata.Namespace(Data.MediaTypeId), JsonSerializer.Serialize(results.Select(result => result.Id.ToString(CultureInfo.InvariantCulture)))));
                return ctx.ReadSql.ReadListAsync<WorkIdentityData>(sql, ct);
            }, CancellationToken);
            var works = known.ToDictionary(identity => identity.Value, identity => identity.WorkId, StringComparer.Ordinal);
            return new BaseDataList<MetadataSearchResultV1>(results.Select(result => new MetadataSearchResultV1(result.Id, Limit(result.Title, 500)!, Limit(result.OriginalTitle, 500),
                result.Year is >= 1800 and <= 2200 ? result.Year : null, Limit(result.Overview, 4000),
                works.TryGetValue(result.Id.ToString(CultureInfo.InvariantCulture), out var workId) ? workId : null)).ToArray());
        }

        private static string? Limit(string? value, int length) => value is null ? null : value.Length <= length ? value : value[..length];
    }

    [ServiceEndpoint("POST", "/api/v1/admin/works/metadata")]
    public sealed class ImportMetadataWorkV1 : AdminService<CreateWorkParametersV1, ImportMetadataWorkDataV1>
    {
        private readonly WorkMetadata _metadata;

        internal ImportMetadataWorkV1(WorkMetadata metadata)
        {
            _metadata = metadata;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(CreateWorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CreateWorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Importing an existing mapping refreshes the same Work.
            var connection = await _metadata.ConnectAsync(Context, CancellationToken);
            var workId = await _metadata.ImportAsync(Context, connection, Data.MediaTypeId, Data.TmdbId, CancellationToken);
            return await Context.ExecuteBatchAsync((ctx, ct) => WorkMetadataQueries.ReadWorkAsync(ctx, workId, ct), CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/metadata")]
    public sealed class RefreshWorkMetadataV1 : AdminService<WorkParametersV1, NoData>
    {
        private readonly WorkMetadata _metadata;

        internal RefreshWorkMetadataV1(WorkMetadata metadata)
        {
            _metadata = metadata;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Only Works mapped to TMDB can be refreshed.
            var (work, identity) = await Context.ExecuteBatchAsync(async (ctx, ct) => (await WorkMetadataQueries.ReadWorkAsync(ctx, Parameters.WorkId, ct),
                await WorkMetadataQueries.ReadTmdbIdentityAsync(ctx, Parameters.WorkId, ct)), CancellationToken);
            if (identity is null)
            {
                throw new DataValidationException(nameof(WorkParametersV1.WorkId), "unknown_reference");
            }

            var connection = await _metadata.ConnectAsync(Context, CancellationToken);
            await _metadata.ImportAsync(Context, connection, work.MediaTypeId, int.Parse(identity.Value, CultureInfo.InvariantCulture), CancellationToken);
            return await Context.ExecuteBatchAsync((ctx, ct) => WorkMetadataQueries.ReadWorkAsync(ctx, Parameters.WorkId, ct), CancellationToken);
        }
    }

    internal static class WorkMetadataQueries
    {
        public static Task<WorkDetailsV1> ReadWorkAsync(LogicContext ctx, long workId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."MediaTypeId",
                    work."CanonicalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    work."Version"
                FROM
                    "Works" AS work
                WHERE
                    work."Id" = @Id;
                """;
            sql.Parameters.Add(new WorkIdData(workId));
            return ctx.ReadSql.ReadRequiredAsync<WorkDetailsV1>(sql, ct);
        }

        public static Task<WorkIdentityData?> ReadTmdbIdentityAsync(LogicContext ctx, long workId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    identity."Namespace",
                    identity."Value",
                    identity."WorkId"
                FROM
                    "WorkExternalIdentities" AS identity
                WHERE
                    identity."WorkId" = @Id
                    AND identity."Namespace" IN ('tmdb_movie', 'tmdb_tv');
                """;
            sql.Parameters.Add(new WorkIdData(workId));
            return ctx.ReadSql.ReadOptionalAsync<WorkIdentityData>(sql, ct);
        }
    }
}
