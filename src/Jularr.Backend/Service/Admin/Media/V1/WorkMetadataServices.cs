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
        private readonly AniListMetadata _aniList;
        private readonly MusicMetadata _music;
        private readonly GameMetadata _games;

        internal SearchMetadataV1(WorkMetadata metadata, BookMetadata books, AniListMetadata aniList, MusicMetadata music, GameMetadata games)
        {
            _metadata = metadata;
            _books = books;
            _aniList = aniList;
            _music = music;
            _games = games;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<MetadataSearchResultV1>>()];
        protected override ModuleRequirement GetInstanceModules(SearchMetadataParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(SearchMetadataParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Books come from Open Library, Manga from AniList, albums from MusicBrainz, video from TMDB.
            IReadOnlyList<MetadataSearchResultV1> results;
            if (BookMetadata.Handles(Data.MediaTypeId, Data.MediaSubtypeId))
            {
                results = [.. (await _books.SearchAsync(Data.Query, Data.ReleaseYear, CancellationToken)).Select(result => new MetadataSearchResultV1(result.WorkId, Limit(result.Title, 500)!,
                    null, result.Year is >= 1800 and <= 2200 ? result.Year : null, null, null, Limit(string.Join(", ", result.Authors), 4000)))];
            }
            else if (GameMetadata.Handles(Data.MediaTypeId))
            {
                var connection = await _games.ConnectAsync(Context, CancellationToken);
                results = [.. (await _games.SearchAsync(connection, Data.Query, Data.ReleaseYear, CancellationToken)).Select(result => new MetadataSearchResultV1(
                    result.Id.ToString(CultureInfo.InvariantCulture), result.Name, null, result.Year is >= 1950 and <= 2200 ? result.Year : null,
                    Limit(string.Join(", ", result.Platforms.Select(platform => platform.Abbreviation ?? platform.Name)), 4000), null, Limit(string.Join(", ", result.Developers), 4000)))];
            }
            else if (MusicMetadata.Handles(Data.MediaTypeId))
            {
                results = [.. (await _music.SearchAsync(Data.Query, Data.ReleaseYear, CancellationToken)).Select(result => new MetadataSearchResultV1(result.Id, result.Title, null,
                    result.Year is >= 1800 and <= 2200 ? result.Year : null, result.PrimaryType, null, result.Artist))];
            }
            else if (AniListMetadata.Handles(Data.MediaTypeId, Data.MediaSubtypeId))
            {
                results = [.. (await _aniList.SearchMangaAsync(Data.Query, Data.ReleaseYear, CancellationToken)).Select(result => new MetadataSearchResultV1(result.Id.ToString(CultureInfo.InvariantCulture),
                    Limit(AniListMetadata.Title(result), 500)!, Limit(result.RomajiTitle, 500), result.Year is >= 1800 and <= 2200 ? result.Year : null, Limit(result.Description, 4000), null,
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
                var space = BookMetadata.Handles(Data.MediaTypeId, Data.MediaSubtypeId) ? BookMetadata.Namespace : AniListMetadata.Handles(Data.MediaTypeId, Data.MediaSubtypeId) ? AniListMetadata.Namespace
                    : MusicMetadata.Handles(Data.MediaTypeId) ? MusicMetadata.Namespace
                    : GameMetadata.Handles(Data.MediaTypeId) ? GameMetadata.Namespace : WorkMetadata.Namespace(Data.MediaTypeId);
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
        private readonly AniListMetadata _aniList;
        private readonly MusicMetadata _music;
        private readonly GameMetadata _games;

        internal ImportMetadataWorkV1(WorkMetadata metadata, BookMetadata books, AniListMetadata aniList, MusicMetadata music, GameMetadata games)
        {
            _metadata = metadata;
            _books = books;
            _aniList = aniList;
            _music = music;
            _games = games;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(CreateWorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CreateWorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Importing an existing mapping refreshes the same Work.
            long workId;
            if (BookMetadata.Handles(Data.MediaTypeId, Data.MediaSubtypeId))
            {
                workId = await _books.ImportAsync(Context, Data.MediaSubtypeId, Data.ExternalId, CancellationToken);
            }
            else if (AniListMetadata.Handles(Data.MediaTypeId, Data.MediaSubtypeId))
            {
                workId = await _aniList.ImportAsync(Context, Data.ExternalId, CancellationToken);
            }
            else if (MusicMetadata.Handles(Data.MediaTypeId))
            {
                workId = await _music.ImportAsync(Context, Data.ExternalId, CancellationToken);
            }
            else if (GameMetadata.Handles(Data.MediaTypeId))
            {
                workId = await _games.ImportAsync(Context, await _games.ConnectAsync(Context, CancellationToken), Data.ExternalId, CancellationToken);
            }
            else
            {
                var connection = await _metadata.ConnectAsync(Context, CancellationToken);
                workId = await _metadata.ImportAsync(Context, connection, Data.MediaTypeId, Data.MediaSubtypeId, int.Parse(Data.ExternalId, CultureInfo.InvariantCulture), CancellationToken);
            }

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
