using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
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
            // Only Works mapped to TMDB, Open Library, AniList, MusicBrainz or IGDB can be refreshed.
            var (work, identity) = await Context.ExecuteBatchAsync(async (ctx, ct) => (await WorkMetadataQueries.ReadWorkAsync(ctx, Parameters.WorkId, ct),
                await WorkMetadataQueries.ReadProviderIdentityAsync(ctx, Parameters.WorkId, ct)), CancellationToken);
            if (identity is null)
            {
                throw new DataValidationException(nameof(WorkParametersV1.WorkId), "unknown_reference");
            }

            if (identity.Namespace == BookMetadata.Namespace)
            {
                await _books.ImportAsync(Context, work.MediaSubtypeId, identity.Value, CancellationToken);
            }
            else if (identity.Namespace == AniListMetadata.Namespace)
            {
                await _aniList.ImportAsync(Context, identity.Value, CancellationToken);
            }
            else if (identity.Namespace == MusicMetadata.Namespace)
            {
                await _music.ImportAsync(Context, identity.Value, CancellationToken);
            }
            else if (identity.Namespace == GameMetadata.Namespace)
            {
                await _games.ImportAsync(Context, await _games.ConnectAsync(Context, CancellationToken), identity.Value, CancellationToken);
            }
            else
            {
                var connection = await _metadata.ConnectAsync(Context, CancellationToken);
                await _metadata.ImportAsync(Context, connection, work.MediaTypeId, null, int.Parse(identity.Value, CultureInfo.InvariantCulture), CancellationToken);
            }
            return await Context.ExecuteBatchAsync((ctx, ct) => WorkMetadataQueries.ReadWorkAsync(ctx, Parameters.WorkId, ct), CancellationToken);
        }
    }

    // Provider search and import of every media type, for the Works page and for Discover.
    internal sealed class MetadataCatalog(WorkMetadata metadata, BookMetadata books, AniListMetadata aniList, MusicMetadata music, GameMetadata games)
    {
        public async Task<IReadOnlyList<MetadataSearchResultV1>> SearchAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, string query, int? year, CancellationToken ct)
        {
            // Books come from Open Library, Manga and Light Novels also from AniList, albums from MusicBrainz, games from IGDB, video from TMDB.
            var results = new List<MetadataSearchResultV1>();
            if (BookMetadata.Handles(mediaType))
            {
                // One unreachable provider leaves the other's results.
                ServiceUnavailableException? failure = null;
                try
                {
                    results.AddRange((await books.SearchAsync(query, year, ct)).Select(result => new MetadataSearchResultV1(BookMetadata.Namespace, result.WorkId,
                        TextLimit.Cut(result.Title, 500)!, null, result.Year is >= 1800 and <= 2200 ? result.Year : null, null, null, TextLimit.Cut(string.Join(", ", result.Authors), 4000))));
                }
                catch (ServiceUnavailableException exception)
                {
                    failure = exception;
                }

                if (subtype is MediaSubtype.Manga or MediaSubtype.LightNovel)
                {
                    results.AddRange((await aniList.SearchBooksAsync(subtype.Value, query, year, ct)).Select(result => new MetadataSearchResultV1(
                        AniListMetadata.Namespace, result.Id.ToString(CultureInfo.InvariantCulture), TextLimit.Cut(AniListMetadata.Title(result), 500)!, TextLimit.Cut(result.RomajiTitle, 500),
                        result.Year is >= 1800 and <= 2200 ? result.Year : null, TextLimit.Cut(result.Description, 4000), null, TextLimit.Cut(string.Join(", ", result.Authors), 4000))));
                }
                else if (failure is not null)
                {
                    throw failure;
                }
            }
            else if (GameMetadata.Handles(mediaType))
            {
                var connection = await games.ConnectAsync(context, ct);
                results.AddRange((await games.SearchAsync(connection, query, year, ct)).Select(result => new MetadataSearchResultV1(GameMetadata.Namespace,
                    result.Id.ToString(CultureInfo.InvariantCulture), result.Name, null, result.Year is >= 1950 and <= 2200 ? result.Year : null,
                    TextLimit.Cut(string.Join(", ", result.Platforms.Select(platform => platform.Abbreviation ?? platform.Name)), 4000), null, TextLimit.Cut(string.Join(", ", result.Developers), 4000))));
            }
            else if (MusicMetadata.Handles(mediaType))
            {
                results.AddRange((await music.SearchAsync(query, year, ct)).Select(result => new MetadataSearchResultV1(MusicMetadata.Namespace, result.Id,
                    result.Title, null, result.Year is >= 1800 and <= 2200 ? result.Year : null, result.PrimaryType, null, result.Artist)));
            }
            else
            {
                var connection = await metadata.ConnectAsync(context, ct);
                results.AddRange((await metadata.SearchAsync(connection, mediaType, query, year, ct)).Select(result =>
                    new MetadataSearchResultV1(WorkMetadata.Namespace(mediaType), result.Id.ToString(CultureInfo.InvariantCulture), TextLimit.Cut(result.Title, 500)!,
                        TextLimit.Cut(result.OriginalTitle, 500), result.Year is >= 1800 and <= 2200 ? result.Year : null, TextLimit.Cut(result.Overview, 4000), null)));
            }

            // Mark results that already are Works.
            var known = await context.ExecuteBatchAsync((ctx, token) =>
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
                        JOIN jsonb_to_recordset(@Values::jsonb) AS item ("Namespace" text, "Value" text) ON item."Namespace" = identity."Namespace" AND item."Value" = identity."Value";
                    """;
                sql.Parameters.Add(new WorkIdentityLookupData(JsonSerializer.Serialize(results.Select(result => new { result.Namespace, Value = result.ExternalId }))));
                return ctx.ReadSql.ReadListAsync<WorkIdentityData>(sql, token);
            }, ct);
            var works = known.ToDictionary(identity => (identity.Namespace, identity.Value), identity => identity.WorkId);
            return [.. results.Select(result => new MetadataSearchResultV1(result.Namespace, result.ExternalId, result.Title, result.OriginalTitle,
                result.ReleaseYear, result.Overview, works.TryGetValue((result.Namespace, result.ExternalId), out var workId) ? workId : null, result.Author))];
        }

        public async Task<long> ImportAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, string @namespace, string externalId, CancellationToken ct)
        {
            // Importing an existing mapping refreshes the same Work.
            if (BookMetadata.Handles(mediaType))
            {
                return @namespace == AniListMetadata.Namespace ? await aniList.ImportAsync(context, externalId, ct) : await books.ImportAsync(context, subtype, externalId, ct);
            }

            if (MusicMetadata.Handles(mediaType))
            {
                return await music.ImportAsync(context, externalId, ct);
            }

            if (GameMetadata.Handles(mediaType))
            {
                return await games.ImportAsync(context, await games.ConnectAsync(context, ct), externalId, ct);
            }

            return await metadata.ImportAsync(context, await metadata.ConnectAsync(context, ct), mediaType, subtype, int.Parse(externalId, CultureInfo.InvariantCulture), ct);
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
                    JOIN "Works" AS work ON work."Id" = identity."WorkId"
                WHERE
                    identity."WorkId" = @Id
                    AND (identity."Namespace" IN ('tmdb_movie', 'tmdb_tv', 'openlibrary', 'musicbrainz', 'igdb') OR identity."Namespace" = 'anilist' AND work."MediaTypeId" = 3)
                ORDER BY
                    identity."Namespace" = 'anilist'
                LIMIT 1;
                """;
            sql.Parameters.Add(new WorkIdData(workId));
            return ctx.ReadSql.ReadOptionalAsync<WorkIdentityData>(sql, ct);
        }
    }
}
