using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/metadata/search")]
    public sealed class SearchMetadataV1 : AdminService<SearchMetadataParametersV1, SearchMetadataDataV1>
    {
        private readonly MetadataCatalog _catalog;

        internal SearchMetadataV1(MetadataCatalog catalog)
        {
            _catalog = catalog;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<MetadataSearchResultV1>>()];
        protected override ModuleRequirement GetInstanceModules(SearchMetadataParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(SearchMetadataParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            return new BaseDataList<MetadataSearchResultV1>(await _catalog.SearchAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Data.Query, Data.ReleaseYear, CancellationToken));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/works/metadata")]
    public sealed class ImportMetadataWorkV1 : AdminService<CreateWorkParametersV1, ImportMetadataWorkDataV1>
    {
        private readonly MetadataCatalog _catalog;

        internal ImportMetadataWorkV1(MetadataCatalog catalog)
        {
            _catalog = catalog;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(CreateWorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CreateWorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var workId = await _catalog.ImportAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Data.Namespace, Data.ExternalId, CancellationToken);
            return await Context.ExecuteBatchAsync((ctx, ct) => WorkMetadataQueries.ReadWorkAsync(ctx, workId, ct), CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/metadata")]
    public sealed class RefreshWorkMetadataV1 : AdminService<WorkParametersV1, NoData>
    {
        private readonly WorkMetadata _metadata;
        private readonly BookMetadata _books;
        private readonly AniListMetadata _aniList;
        private readonly MusicMetadata _music;
        private readonly GameMetadata _games;

        internal RefreshWorkMetadataV1(WorkMetadata metadata, BookMetadata books, AniListMetadata aniList, MusicMetadata music, GameMetadata games)
        {
            _metadata = metadata;
            _books = books;
            _aniList = aniList;
            _music = music;
            _games = games;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            await RefreshAsync(Context, _metadata, _books, _aniList, _music, _games, Parameters.WorkId, CancellationToken);
            return await Context.ExecuteBatchAsync((ctx, ct) => WorkMetadataQueries.ReadWorkAsync(ctx, Parameters.WorkId, ct), CancellationToken);
        }

        // Only Works mapped to TMDB, TheTVDB, Open Library, AniList, MusicBrainz or IGDB can be refreshed; the mass editor does the same for each chosen title.
        internal static async Task RefreshAsync(LogicContext context, WorkMetadata metadata, BookMetadata books, AniListMetadata aniList, MusicMetadata music, GameMetadata games, long workId, CancellationToken ct)
        {
            var (work, identity) = await context.ExecuteBatchAsync(async (ctx, token) => (await WorkMetadataQueries.ReadWorkAsync(ctx, workId, token),
                await WorkMetadataQueries.ReadProviderIdentityAsync(ctx, workId, token)), ct);
            if (identity is null)
            {
                throw new DataValidationException(nameof(WorkParametersV1.WorkId), "unknown_reference");
            }

            if (identity.Namespace == BookMetadata.Namespace)
            {
                await books.ImportAsync(context, work.MediaSubtypeId, identity.Value, ct);
            }
            else if (identity.Namespace == AniListMetadata.Namespace)
            {
                await aniList.ImportAsync(context, identity.Value, ct);
            }
            else if (identity.Namespace == MusicMetadata.Namespace)
            {
                await music.ImportAsync(context, identity.Value, ct);
            }
            else if (identity.Namespace == GameMetadata.Namespace)
            {
                await games.ImportAsync(context, await games.ConnectAsync(context, ct), identity.Value, ct);
            }
            else if (work.MediaTypeId == MediaType.Series)
            {
                await metadata.ImportSeriesAsync(context, null, identity.Namespace, identity.Value, ct);
            }
            else
            {
                var connection = await metadata.ConnectAsync(context, ct);
                await metadata.ImportAsync(context, connection, work.MediaTypeId, null, int.Parse(identity.Value, CultureInfo.InvariantCulture), ct);
            }
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/match")]
    public sealed class RematchWorkV1 : AdminService<WorkParametersV1, RematchWorkDataV1>
    {
        private readonly MetadataCatalog _catalog;

        internal RematchWorkV1(MetadataCatalog catalog)
        {
            _catalog = catalog;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The new entry replaces the old match, then its metadata fills the same Work.
            var work = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var current = await WorkMetadataQueries.ReadWorkAsync(ctx, Parameters.WorkId, ct);
                await ctx.RematchWorkAsync(new WorkRematchData(current.Id, Data.Namespace, Data.ExternalId, Data.Title, Data.ReleaseYear, Data.Version), current.MediaTypeId, ct);
                return current;
            }, CancellationToken);
            await _catalog.ImportAsync(Context, work.MediaTypeId, work.MediaSubtypeId, Data.Namespace, Data.ExternalId, CancellationToken);
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
                    work."MediaSubtypeId",
                    work."IsMediaSubtypeManual",
                    work."CanonicalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    work."Version",
                    (SELECT wanted."Id" FROM "WantedItems" AS wanted WHERE wanted."WorkId" = work."Id" ORDER BY wanted."Id" LIMIT 1) AS "WantedItemId",
                    franchise."Name" AS "FranchiseName",
                    work."FranchisePosition",
                    work."IsFranchiseManual"
                FROM
                    "Works" AS work
                    LEFT JOIN "Franchises" AS franchise ON franchise."Id" = work."FranchiseId"
                WHERE
                    work."Id" = @Id;
                """;
            sql.Parameters.Add(new WorkIdData(workId));
            return ctx.ReadSql.ReadRequiredAsync<WorkDetailsV1>(sql, ct);
        }

        // The identity a Work's details come from; a series is TheTVDB's and is found by its TMDB or IMDb id only while it holds no TheTVDB id.
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
                    JOIN "Works" AS work ON work."Id" = identity."WorkId"
                WHERE
                    identity."WorkId" = @Id
                    AND (identity."Namespace" IN ('tmdb_movie', 'openlibrary', 'musicbrainz', 'igdb')
                        OR identity."Namespace" IN ('tvdb', 'tmdb_tv', 'imdb') AND work."MediaTypeId" = 2
                        OR identity."Namespace" = 'anilist' AND work."MediaTypeId" = 3
                        OR identity."Namespace" = 'syosetu' AND work."MediaTypeId" = 3)
                ORDER BY
                    identity."Namespace" = 'anilist',
                    identity."Namespace" = 'imdb',
                    identity."Namespace" = 'tmdb_tv'
                LIMIT 1;
                """;
            sql.Parameters.Add(new WorkIdData(workId));
            return ctx.ReadSql.ReadOptionalAsync<WorkIdentityData>(sql, ct);
        }
    }
}
