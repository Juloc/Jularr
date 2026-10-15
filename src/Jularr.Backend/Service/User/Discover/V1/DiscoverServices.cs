using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Tmdb;
using Jularr.Integrations.Logic.Tmdb;

namespace Jularr.Backend.Service.User.Discover.V1
{
    [ServiceEndpoint("POST", "/api/v1/discover/feed")]
    public sealed class ReadDiscoverFeedV1 : UserService<DiscoverParametersV1, ReadDiscoverFeedDataV1>
    {
        private readonly WorkMetadata _metadata;

        internal ReadDiscoverFeedV1(WorkMetadata metadata)
        {
            _metadata = metadata;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<DiscoverItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(DiscoverParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Series have no truthful upcoming list.
            if (Data.MediaTypeId is not (MediaType.Movie or MediaType.Series) || Data.MediaTypeId == MediaType.Series && Data.DiscoverFeedTypeId == DiscoverFeedType.Upcoming)
            {
                throw new DataValidationException(nameof(ReadDiscoverFeedDataV1.DiscoverFeedTypeId), "unknown_catalog_value");
            }

            // Only types the profile lists.
            if (!(await DiscoverQueries.ReadTypesAsync(Context, Auth.ProfileId!.Value, CancellationToken)).Listed.Contains(Data.MediaTypeId))
            {
                return new BaseDataList<DiscoverItemV1>([]);
            }

            var connection = await _metadata.ConnectAsync(Context, CancellationToken);
            var results = await _metadata.ReadFeedAsync(connection, Data.MediaTypeId, Data.DiscoverFeedTypeId, Data.Page, CancellationToken);
            var overlay = await DiscoverQueries.ReadOverlayAsync(Context, Data.MediaTypeId, results.Select(result => result.Id), Auth.ProfileId!.Value, CancellationToken);
            return new BaseDataList<DiscoverItemV1>(results.Select(result => DiscoverQueries.ToItem(Data.MediaTypeId, result, overlay)).ToArray());
        }
    }

    [ServiceEndpoint("POST", "/api/v1/discover/search")]
    public sealed class SearchDiscoverV1 : UserService<DiscoverParametersV1, SearchDiscoverDataV1>
    {
        private const int LocalLimit = 20;
        private readonly WorkMetadata _metadata;

        internal SearchDiscoverV1(WorkMetadata metadata)
        {
            _metadata = metadata;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<DiscoverItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(DiscoverParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Local titles first; they never need the provider.
            var local = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        work."MediaTypeId",
                        tmdb."Value"::integer AS "TmdbId",
                        work."Id" AS "WorkId",
                        work."CanonicalTitle" AS "Title",
                        work."ReleaseYear"::integer AS "ReleaseYear",
                        overview."Value" AS "Overview",
                        poster."Value" AS "PosterUrl",
                        backdrop."Value" AS "BackdropUrl",
                        state."MediaStateTypeId",
                        (
                            SELECT
                                'https://app.plex.tv/desktop/#!/server/' || provider."ExternalId" || '/details?key=%2Flibrary%2Fmetadata%2F' || plex."Value"
                            FROM
                                "WorkExternalIdentities" AS plex
                                JOIN "Providers" AS provider ON provider."Key" = 'plex' AND provider."IsEnabled" AND provider."ExternalId" IS NOT NULL
                            WHERE
                                plex."WorkId" = work."Id"
                                AND plex."Namespace" = 'plex'
                        ) AS "PlexUrl"
                    FROM
                        "ProfileWorks" AS visible
                        JOIN "Works" AS work ON work."Id" = visible."WorkId"
                        JOIN "ProfileWorkStates" AS state ON state."ProfileId" = visible."ProfileId" AND state."WorkId" = work."Id"
                        LEFT JOIN "WorkExternalIdentities" AS tmdb ON tmdb."WorkId" = work."Id" AND tmdb."Namespace" IN ('tmdb_movie', 'tmdb_tv')
                        LEFT JOIN LATERAL (
                            SELECT
                                fact."Value"
                            FROM
                                "WorkMetadataFacts" AS fact
                            WHERE
                                fact."WorkId" = work."Id"
                                AND fact."FactKey" = 'Overview'
                            ORDER BY
                                fact."IsManual" DESC
                            LIMIT 1
                        ) AS overview ON TRUE
                        LEFT JOIN LATERAL (
                            SELECT
                                fact."Value"
                            FROM
                                "WorkMetadataFacts" AS fact
                            WHERE
                                fact."WorkId" = work."Id"
                                AND fact."FactKey" = 'Poster'
                            ORDER BY
                                fact."IsManual" DESC
                            LIMIT 1
                        ) AS poster ON TRUE
                        LEFT JOIN LATERAL (
                            SELECT
                                fact."Value"
                            FROM
                                "WorkMetadataFacts" AS fact
                            WHERE
                                fact."WorkId" = work."Id"
                                AND fact."FactKey" = 'Backdrop'
                            ORDER BY
                                fact."IsManual" DESC
                            LIMIT 1
                        ) AS backdrop ON TRUE
                    WHERE
                        visible."ProfileId" = @ProfileId
                        AND NOT visible."IsHidden"
                        AND (@MediaTypeId::smallint IS NULL OR visible."MediaTypeId" = @MediaTypeId AND visible."MediaSubtypeId" IS NULL)
                        AND EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkTitles" AS title
                            WHERE
                                title."WorkId" = work."Id"
                                AND title."Title" ILIKE '%' || replace(replace(replace(@Query, '\', '\\'), '%', '\%'), '_', '\_') || '%'
                        )
                    ORDER BY
                        work."CanonicalTitle",
                        work."Id"
                    LIMIT @PageSize
                    OFFSET @Offset;
                    """;
                sql.Parameters.Add(new LibraryFilterData(Data.MediaTypeId, null, Data.Query));
                sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
                sql.Page = new PageRequest(Data.Page, LocalLimit);
                return ctx.ReadSql.ReadListAsync<DiscoverItemV1>(sql, ct);
            }, CancellationToken);

            // Provider results add what is not local yet; an unavailable provider leaves the local results.
            var items = local.Take(LocalLimit).ToList();
            try
            {
                var connection = await _metadata.ConnectAsync(Context, CancellationToken);
                var listed = (await DiscoverQueries.ReadTypesAsync(Context, Auth.ProfileId!.Value, CancellationToken)).Listed;
                foreach (var mediaType in Data.MediaTypeId is { } selected ? [selected] : new[] { MediaType.Movie, MediaType.Series })
                {
                    if (mediaType is not (MediaType.Movie or MediaType.Series) || !listed.Contains(mediaType))
                    {
                        continue;
                    }

                    var results = await _metadata.SearchAsync(connection, mediaType, Data.Query, null, CancellationToken, Data.Page);
                    var overlay = await DiscoverQueries.ReadOverlayAsync(Context, mediaType, results.Select(result => result.Id), Auth.ProfileId!.Value, CancellationToken);
                    var localIds = items.Where(item => item.MediaTypeId == mediaType && item.TmdbId is not null).Select(item => item.TmdbId!.Value).ToHashSet();
                    items.AddRange(results.Where(result => !localIds.Contains(result.Id)).Select(result => DiscoverQueries.ToItem(mediaType, result, overlay)));
                }
            }
            catch (ServiceUnavailableException)
            {
            }

            return new BaseDataList<DiscoverItemV1>(items.ToArray());
        }
    }

    [ServiceEndpoint("POST", "/api/v1/discover/{mediaTypeId}/{tmdbId:int}")]
    public sealed class ReadDiscoverItemV1 : UserService<DiscoverItemParametersV1, NoData>
    {
        private readonly WorkMetadata _metadata;

        internal ReadDiscoverItemV1(WorkMetadata metadata)
        {
            _metadata = metadata;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DiscoverItemV1>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverItemParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(DiscoverItemParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // A provider title before it becomes a Work, of a type the profile may use.
            await DiscoverQueries.RequireTypeAsync(Context, Parameters.MediaTypeId, Auth.ProfileId!.Value, CancellationToken);
            var connection = await _metadata.ConnectAsync(Context, CancellationToken);
            var work = await _metadata.ReadWorkAsync(connection, Parameters.MediaTypeId, Parameters.TmdbId, CancellationToken);
            var overlay = await DiscoverQueries.ReadOverlayAsync(Context, Parameters.MediaTypeId, [work.Id], Auth.ProfileId!.Value, CancellationToken);
            var result = new TmdbSearchResult(work.Id, work.Title, work.OriginalTitle, work.Year, work.Overview, work.PosterPath, work.BackdropPath);
            return DiscoverQueries.ToItem(Parameters.MediaTypeId, result, overlay);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/discover/{mediaTypeId}/{tmdbId:int}/requests")]
    public sealed class RequestDiscoverItemV1 : UserService<DiscoverItemParametersV1, CreateRequestDataV1>
    {
        private readonly WorkMetadata _metadata;

        internal RequestDiscoverItemV1(WorkMetadata metadata)
        {
            _metadata = metadata;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateRequestResultV1>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverItemParametersV1 parameters) => ModuleRequirement.All("Library", "Requests");
        protected override ServicePermission GetPermission(DiscoverItemParametersV1 parameters, Type resultType) => new("profile.requests.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The provider identity resolves to one canonical Work before the request exists.
            await DiscoverQueries.RequireTypeAsync(Context, Parameters.MediaTypeId, Auth.ProfileId!.Value, CancellationToken);
            var connection = await _metadata.ConnectAsync(Context, CancellationToken);
            var workId = await _metadata.ImportAsync(Context, connection, Parameters.MediaTypeId, null, Parameters.TmdbId, CancellationToken);
            var request = await Context.ExecuteBatchAsync((ctx, ct) => ctx.CreateAcquisitionRequestAsync(new AcquisitionRequestCreationData(workId, Auth.ProfileId!.Value, Auth.AccountId!.Value, Data.UiLocaleId, null, null, Auth.AccountRoleTypeId == AccountRoleType.Owner), ct),
                CancellationToken);
            return new CreateRequestResultV1(request.Id);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/discover/catalog/search")]
    public sealed class SearchDiscoverCatalogV1 : UserService<DiscoverParametersV1, SearchDiscoverCatalogDataV1>
    {
        private readonly MetadataCatalog _catalog;

        internal SearchDiscoverCatalogV1(MetadataCatalog catalog)
        {
            _catalog = catalog;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<DiscoverCatalogItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(DiscoverParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            await DiscoverQueries.RequireCatalogTypeAsync(Context, Data.MediaTypeId, Auth.ProfileId!.Value, CancellationToken);
            var results = await _catalog.SearchAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Data.Query, null, CancellationToken);
            return new BaseDataList<DiscoverCatalogItemV1>([.. results.Select(DiscoverQueries.CatalogItem)]);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/discover/catalog/feed")]
    public sealed class ReadDiscoverCatalogFeedV1 : UserService<DiscoverParametersV1, ReadDiscoverCatalogFeedDataV1>
    {
        private readonly MetadataCatalog _catalog;

        internal ReadDiscoverCatalogFeedV1(MetadataCatalog catalog)
        {
            _catalog = catalog;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<DiscoverCatalogItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(DiscoverParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            await DiscoverQueries.RequireCatalogTypeAsync(Context, Data.MediaTypeId, Auth.ProfileId!.Value, CancellationToken);
            var results = await _catalog.ReadFeedAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Data.DiscoverFeedTypeId, CancellationToken);
            return new BaseDataList<DiscoverCatalogItemV1>([.. results.Select(DiscoverQueries.CatalogItem)]);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/discover/catalog/requests")]
    public sealed class RequestDiscoverCatalogItemV1 : UserService<DiscoverParametersV1, RequestDiscoverCatalogDataV1>
    {
        private readonly MetadataCatalog _catalog;

        internal RequestDiscoverCatalogItemV1(MetadataCatalog catalog)
        {
            _catalog = catalog;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateRequestResultV1>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverParametersV1 parameters) => ModuleRequirement.All("Library", "Requests");
        protected override ServicePermission GetPermission(DiscoverParametersV1 parameters, Type resultType) => new("profile.requests.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The catalog identity resolves to one canonical Work before the request exists.
            await DiscoverQueries.RequireCatalogTypeAsync(Context, Data.MediaTypeId, Auth.ProfileId!.Value, CancellationToken);
            var workId = await _catalog.ImportAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Data.Namespace, Data.ExternalId, CancellationToken);
            var request = await Context.ExecuteBatchAsync((ctx, ct) => ctx.CreateAcquisitionRequestAsync(new AcquisitionRequestCreationData(workId, Auth.ProfileId!.Value, Auth.AccountId!.Value,
                Data.UiLocaleId, null, null, Auth.AccountRoleTypeId == AccountRoleType.Owner), ct), CancellationToken);
            return new CreateRequestResultV1(request.Id);
        }
    }

    internal static class DiscoverQueries
    {
        public static DiscoverCatalogItemV1 CatalogItem(MetadataSearchResultV1 result) =>
            new(result.Namespace, result.ExternalId, result.Title, result.ReleaseYear, result.Author, result.WorkId, result.PosterUrl);

        // Films and series are discovered through TMDB; the other types through their catalogs.
        public static Task RequireCatalogTypeAsync(LogicContext context, MediaType mediaType, long profileId, CancellationToken ct) =>
            mediaType is MediaType.Book or MediaType.Music or MediaType.Game
                ? RequireTypeAsync(context, mediaType, profileId, ct)
                : throw new DataValidationException("MediaTypeId", "unknown_catalog_value");

        public static async Task<(IReadOnlySet<MediaType> Usable, IReadOnlySet<MediaType> Listed)> ReadTypesAsync(LogicContext context, long profileId, CancellationToken ct)
        {
            var categories = await context.ExecuteBatchAsync((ctx, token) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        category."Key",
                        category."MediaSubtypeId" IS NOT NULL AS "IsSubtype",
                        category."IsHidden"
                    FROM
                        "ProfileMediaCategories" AS category
                    WHERE
                        category."ProfileId" = @ProfileId
                        AND category."MediaSubtypeId" IS NULL;
                    """;
                sql.Parameters.Add(new ProfileParameters(profileId));
                return ctx.ReadSql.ReadListAsync<ProfileMediaV1>(sql, token);
            }, ct);
            return (categories.Select(category => Enum.Parse<MediaType>(category.Key)).ToHashSet(),
                categories.Where(category => !category.IsHidden).Select(category => Enum.Parse<MediaType>(category.Key)).ToHashSet());
        }

        public static async Task RequireTypeAsync(LogicContext context, MediaType mediaType, long profileId, CancellationToken ct)
        {
            if (!(await ReadTypesAsync(context, profileId, ct)).Usable.Contains(mediaType))
            {
                throw new KeyNotFoundException("The media type is not available.");
            }
        }

        public static async Task<IReadOnlyDictionary<int, DiscoverOverlayData>> ReadOverlayAsync(LogicContext context, MediaType mediaType, IEnumerable<int> tmdbIds, long profileId,
            CancellationToken ct)
        {
            // Provider titles that already are Works carry their viewer state.
            var ids = JsonSerializer.Serialize(tmdbIds.Select(id => new { Namespace = WorkMetadata.Namespace(mediaType), Value = id.ToString(CultureInfo.InvariantCulture) }));
            var rows = await context.ExecuteBatchAsync((ctx, token) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        identity."Value",
                        identity."WorkId",
                        state."MediaStateTypeId",
                        (
                            SELECT
                                'https://app.plex.tv/desktop/#!/server/' || provider."ExternalId" || '/details?key=%2Flibrary%2Fmetadata%2F' || plex."Value"
                            FROM
                                "WorkExternalIdentities" AS plex
                                JOIN "Providers" AS provider ON provider."Key" = 'plex' AND provider."IsEnabled" AND provider."ExternalId" IS NOT NULL
                            WHERE
                                plex."WorkId" = identity."WorkId"
                                AND plex."Namespace" = 'plex'
                        ) AS "PlexUrl"
                    FROM
                        "WorkExternalIdentities" AS identity
                        JOIN "ProfileWorks" AS visible ON visible."WorkId" = identity."WorkId" AND visible."ProfileId" = @ProfileId
                        JOIN "ProfileWorkStates" AS state ON state."ProfileId" = visible."ProfileId" AND state."WorkId" = identity."WorkId"
                        JOIN jsonb_to_recordset(@Values::jsonb) AS item ("Namespace" text, "Value" text) ON item."Namespace" = identity."Namespace" AND item."Value" = identity."Value";
                    """;
                sql.Parameters.Add(new WorkIdentityLookupData(ids));
                sql.Parameters.Add(new ProfileParameters(profileId));
                return ctx.ReadSql.ReadListAsync<DiscoverOverlayData>(sql, token);
            }, ct);
            return rows.ToDictionary(row => int.Parse(row.Value, CultureInfo.InvariantCulture));
        }

        public static DiscoverItemV1 ToItem(MediaType mediaType, TmdbSearchResult result, IReadOnlyDictionary<int, DiscoverOverlayData> overlay)
        {
            var known = overlay.GetValueOrDefault(result.Id);
            return new DiscoverItemV1(mediaType, result.Id, known?.WorkId, TextLimit.Cut(result.Title, 500)!, result.Year is >= 1800 and <= 2200 ? result.Year : null, TextLimit.Cut(result.Overview, 4000),
                TmdbClient.PosterUrl(result.PosterPath), TmdbClient.BackdropUrl(result.BackdropPath), known?.MediaStateTypeId ?? MediaStateType.NotRequested, known?.PlexUrl);
        }
    }
}
