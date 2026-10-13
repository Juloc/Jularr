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
        private readonly BookMetadata _books;
        private readonly MangaMetadata _manga;

        internal SearchMetadataV1(WorkMetadata metadata, BookMetadata books, MangaMetadata manga)
        {
            _metadata = metadata;
            _books = books;
            _manga = manga;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<MetadataSearchResultV1>>()];
        protected override ModuleRequirement GetInstanceModules(SearchMetadataParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(SearchMetadataParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Books come from Open Library, Manga from AniList, video from TMDB.
            IReadOnlyList<MetadataSearchResultV1> results;
            if (BookMetadata.Handles(Data.MediaTypeId))
            {
                results = [.. (await _books.SearchAsync(Data.Query, Data.ReleaseYear, CancellationToken)).Select(result => new MetadataSearchResultV1(result.WorkId, Limit(result.Title, 500)!,
                    null, result.Year is >= 1800 and <= 2200 ? result.Year : null, null, null, Limit(string.Join(", ", result.Authors), 4000)))];
            }
            else if (MangaMetadata.Handles(Data.MediaTypeId))
            {
                results = [.. (await _manga.SearchAsync(Data.Query, Data.ReleaseYear, CancellationToken)).Select(result => new MetadataSearchResultV1(result.Id.ToString(CultureInfo.InvariantCulture),
                    Limit(MangaMetadata.Title(result), 500)!, Limit(result.RomajiTitle, 500), result.Year is >= 1800 and <= 2200 ? result.Year : null, Limit(result.Description, 4000), null,
                    Limit(string.Join(", ", result.Authors), 4000)))];
            }
            else
            {
                var connection = await _metadata.ConnectAsync(Context, CancellationToken);
                results = [.. (await _metadata.SearchAsync(connection, Data.MediaTypeId, Data.Query, Data.ReleaseYear, CancellationToken)).Select(result =>
                    new MetadataSearchResultV1(result.Id.ToString(CultureInfo.InvariantCulture), Limit(result.Title, 500)!, Limit(result.OriginalTitle, 500),
                        result.Year is >= 1800 and <= 2200 ? result.Year : null, Limit(result.Overview, 4000), null))];
            }

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
                var space = BookMetadata.Handles(Data.MediaTypeId) ? BookMetadata.Namespace : MangaMetadata.Handles(Data.MediaTypeId) ? MangaMetadata.Namespace : WorkMetadata.Namespace(Data.MediaTypeId);
                sql.Parameters.Add(new WorkIdentityLookupData(space, JsonSerializer.Serialize(results.Select(result => result.ExternalId))));
                return ctx.ReadSql.ReadListAsync<WorkIdentityData>(sql, ct);
            }, CancellationToken);
            var works = known.ToDictionary(identity => identity.Value, identity => identity.WorkId, StringComparer.Ordinal);
            return new BaseDataList<MetadataSearchResultV1>(results.Select(result => new MetadataSearchResultV1(result.ExternalId, result.Title, result.OriginalTitle, result.ReleaseYear,
                result.Overview, works.TryGetValue(result.ExternalId, out var workId) ? workId : null, result.Author)).ToArray());
        }

        private static string? Limit(string? value, int length) => value is null ? null : value.Length <= length ? value : value[..length];
    }

    [ServiceEndpoint("POST", "/api/v1/admin/works/metadata")]
    public sealed class ImportMetadataWorkV1 : AdminService<CreateWorkParametersV1, ImportMetadataWorkDataV1>
    {
        private readonly WorkMetadata _metadata;
        private readonly BookMetadata _books;
        private readonly MangaMetadata _manga;

        internal ImportMetadataWorkV1(WorkMetadata metadata, BookMetadata books, MangaMetadata manga)
        {
            _metadata = metadata;
            _books = books;
            _manga = manga;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(CreateWorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CreateWorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Importing an existing mapping refreshes the same Work.
            long workId;
            if (BookMetadata.Handles(Data.MediaTypeId))
            {
                workId = await _books.ImportAsync(Context, Data.MediaTypeId, Data.ExternalId, CancellationToken);
            }
            else if (MangaMetadata.Handles(Data.MediaTypeId))
            {
                workId = await _manga.ImportAsync(Context, Data.ExternalId, CancellationToken);
            }
            else
            {
                var connection = await _metadata.ConnectAsync(Context, CancellationToken);
                workId = await _metadata.ImportAsync(Context, connection, Data.MediaTypeId, int.Parse(Data.ExternalId, CultureInfo.InvariantCulture), CancellationToken);
            }

            return await Context.ExecuteBatchAsync((ctx, ct) => WorkMetadataQueries.ReadWorkAsync(ctx, workId, ct), CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/metadata")]
    public sealed class RefreshWorkMetadataV1 : AdminService<WorkParametersV1, NoData>
    {
        private readonly WorkMetadata _metadata;
        private readonly BookMetadata _books;
        private readonly MangaMetadata _manga;

        internal RefreshWorkMetadataV1(WorkMetadata metadata, BookMetadata books, MangaMetadata manga)
        {
            _metadata = metadata;
            _books = books;
            _manga = manga;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Only Works mapped to TMDB, Open Library or AniList can be refreshed.
            var (work, identity) = await Context.ExecuteBatchAsync(async (ctx, ct) => (await WorkMetadataQueries.ReadWorkAsync(ctx, Parameters.WorkId, ct),
                await WorkMetadataQueries.ReadProviderIdentityAsync(ctx, Parameters.WorkId, ct)), CancellationToken);
            if (identity is null)
            {
                throw new DataValidationException(nameof(WorkParametersV1.WorkId), "unknown_reference");
            }

            if (identity.Namespace == BookMetadata.Namespace)
            {
                await _books.ImportAsync(Context, work.MediaTypeId, identity.Value, CancellationToken);
            }
            else if (identity.Namespace == MangaMetadata.Namespace)
            {
                await _manga.ImportAsync(Context, identity.Value, CancellationToken);
            }
            else
            {
                var connection = await _metadata.ConnectAsync(Context, CancellationToken);
                await _metadata.ImportAsync(Context, connection, work.MediaTypeId, int.Parse(identity.Value, CultureInfo.InvariantCulture), CancellationToken);
            }
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

        public static Task<WorkIdentityData?> ReadProviderIdentityAsync(LogicContext ctx, long workId, CancellationToken ct)
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
                    AND identity."Namespace" IN ('tmdb_movie', 'tmdb_tv', 'openlibrary', 'anilist');
                """;
            sql.Parameters.Add(new WorkIdData(workId));
            return ctx.ReadSql.ReadOptionalAsync<WorkIdentityData>(sql, ct);
        }
    }
}
