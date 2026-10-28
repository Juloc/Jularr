using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Tmdb;
using UserMedia = Jularr.Backend.Data.User.Media.V1;

namespace Jularr.Backend.Service.User.Discover.V1
{
    [ServiceEndpoint("POST", "/api/v1/discover/feed")]
    public sealed class ReadDiscoverFeedV1 : UserService<DiscoverParametersV1, ReadDiscoverFeedDataV1>
    {
        private readonly WorkMetadata _metadata;
        private readonly TimeProvider _time;
        private readonly ExternalFeedCache _feeds;

        internal ReadDiscoverFeedV1(WorkMetadata metadata, TimeProvider time, ExternalFeedCache feeds)
        {
            _metadata = metadata;
            _time = time;
            _feeds = feeds;
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

            var list = new ExternalWorkListData(WorkMetadata.Namespace(Data.MediaTypeId), Data.MediaTypeId, null);
            var feed = new ExternalFeedData(list.Namespace, Data.MediaTypeId, null, Data.DiscoverFeedTypeId, Data.Page, null, null);
            var local = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                if (!(await DiscoverQueries.ReadTypesInBatchAsync(ctx, Auth.ProfileId!.Value, ct)).Listed.Contains(Data.MediaTypeId))
                {
                    return (Keeps: false, Cached: (BaseDataList<DiscoverItemV1>?)new([]));
                }
                if ((await ctx.ReadWholeExternalWorkCatalogsAsync(ct)).Contains(list.Namespace))
                {
                    var shelf = new ExternalWorkShelfData(0, list.Namespace, Data.MediaTypeId, null, Data.DiscoverFeedTypeId);
                    var rows = await ctx.ReadExternalWorkShelvesAsync(new ExternalWorkShelfQueryData([shelf], Auth.ProfileId!.Value,
                        DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime), (Data.Page - 1) * ExternalWorkLogic.ShelfSize), ct);
                    return (Keeps: true, Cached: (BaseDataList<DiscoverItemV1>?)new(rows.Select(row => DiscoverQueries.ToItem(Data.MediaTypeId, row)).ToArray()));
                }
                var keeps = await ctx.KeepsDiscoverListsAsync(ct);
                if (keeps)
                {
                    var snapshot = await ExternalFeedCache.ReadAsync(ctx, feed, ct);
                    if (snapshot.Items is { } cached)
                    {
                        return (Keeps: true, Cached: (BaseDataList<DiscoverItemV1>?)await DiscoverQueries.ItemsInBatchAsync(ctx, Data.MediaTypeId, cached, Auth.ProfileId!.Value, ct));
                    }
                }
                return (Keeps: keeps, Cached: (BaseDataList<DiscoverItemV1>?)null);
            }, CancellationToken, mode: SqlAccessMode.ReadOnly);
            if (local.Cached is not null)
            {
                return local.Cached;
            }

            IReadOnlyList<ExternalWorkData> works;
            if (local.Keeps)
            {
                works = await _feeds.LoadAsync(Context, feed, (entry, ct) => _metadata.FetchExternalFeedAsync(Context, entry, ct), CancellationToken);
            }
            else
            {
                // Without kept lists the titles shown still become Works, so each card opens one; a series is TheTVDB's.
                var connection = await _metadata.ConnectAsync(Context, CancellationToken);
                var results = await _metadata.ReadFeedAsync(connection, Data.MediaTypeId, Data.DiscoverFeedTypeId, Data.Page, CancellationToken);
                works = await MetadataCatalog.KeepWorksAsync(Context, new ExternalWorkListData(ExternalFeedLogic.CatalogOf(list.Namespace), Data.MediaTypeId, null),
                    await _metadata.ExternalWorksAsync(Context, connection, Data.MediaTypeId, results, CancellationToken), CancellationToken);
            }

            return await DiscoverQueries.ItemsAsync(Context, Data.MediaTypeId, works, Auth.ProfileId!.Value, CancellationToken);
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
            // Local titles first; they never need the provider. A catalog title nobody opened is no local title.
            var local = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        work."MediaTypeId",
                        tmdb."Value"::integer AS "TmdbId",
                        work."Id" AS "WorkId",
                        visible."Title" AS "Title",
                        work."ReleaseYear"::integer AS "ReleaseYear",
                        visible."Overview" AS "Overview",
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
                        ) AS "PlexUrl",
                        work."Rating"
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
                        AND (work."DetailedAt" IS NOT NULL OR EXISTS (SELECT 1 FROM "WorkLibraryRelations" AS library WHERE library."WorkId" = work."Id"))
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
                        visible."Title",
                        work."Id"
                    LIMIT @PageSize
                    OFFSET @Offset;
                    """;
                sql.Parameters.Add(new LibraryFilterData(Data.MediaTypeId, null, Data.Query));
                sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
                sql.Page = new PageRequest(Data.Page, LocalLimit);
                return ctx.ReadSql.ReadListAsync<DiscoverItemV1>(sql, ct);
            }, CancellationToken, mode: SqlAccessMode.ReadOnly);

            // Provider results add what is not local yet: films from TMDB, series from TheTVDB. An unavailable provider leaves the other type and the local results;
            // a search of series alone says when TheTVDB has no key.
            var items = local.Take(LocalLimit).ToList();
            var listed = (await DiscoverQueries.ReadTypesAsync(Context, Auth.ProfileId!.Value, CancellationToken)).Listed;
            foreach (var mediaType in Data.MediaTypeId is { } selected ? [selected] : new[] { MediaType.Movie, MediaType.Series })
            {
                if (mediaType is not (MediaType.Movie or MediaType.Series) || !listed.Contains(mediaType))
                {
                    continue;
                }

                try
                {
                    // Every result is kept as the Work it is; one the local results already hold is not repeated.
                    IReadOnlyList<ExternalWorkData> results = mediaType == MediaType.Series
                        ? await _metadata.SearchSeriesAsync(Context, Data.Query, null, CancellationToken, Data.Page)
                        : [.. (await _metadata.SearchAsync(await _metadata.ConnectAsync(Context, CancellationToken), mediaType, Data.Query, null, CancellationToken, Data.Page))
                            .Select(result => WorkMetadata.ExternalWork(WorkMetadata.Namespace(mediaType), result))];
                    var list = new ExternalWorkListData(mediaType == MediaType.Series ? "tvdb" : WorkMetadata.Namespace(mediaType), mediaType, null);
                    var works = await MetadataCatalog.KeepWorksAsync(Context, list, results, CancellationToken);
                    var localIds = items.Select(item => item.WorkId).ToHashSet();
                    items.AddRange((await DiscoverQueries.ItemsAsync(Context, mediaType, works, Auth.ProfileId!.Value, CancellationToken)).Where(item => !localIds.Contains(item.WorkId)));
                }
                catch (ServiceUnavailableException exception) when (exception.Code != "tvdb_key_missing" || Data.MediaTypeId is null)
                {
                }
            }

            return new BaseDataList<DiscoverItemV1>(items.ToArray());
        }
    }

    [ServiceEndpoint("POST", "/api/v1/works/{workId:long}/recommendations")]
    public sealed class ReadWorkRecommendationsV1 : UserService<UserMedia.WorkParametersV1, NoData>
    {
        private readonly WorkMetadata _metadata;
        private readonly ExternalFeedCache _feeds;

        internal ReadWorkRecommendationsV1(WorkMetadata metadata, ExternalFeedCache feeds)
        {
            _metadata = metadata;
            _feeds = feeds;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<DiscoverItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(UserMedia.WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(UserMedia.WorkParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // TMDB's recommendations for a film or series the profile sees, in the profile's types; other Works have none.
            var local = await Context.ExecuteBatchAsync(async (ctx, ct) =>
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
                        JOIN "ProfileWorks" AS visible ON visible."WorkId" = identity."WorkId" AND visible."ProfileId" = @ProfileId
                    WHERE
                        identity."WorkId" = @WorkId
                        AND identity."Namespace" IN ('tmdb_movie', 'tmdb_tv')
                    LIMIT 1;
                    """;
                sql.Parameters.Add(Parameters);
                sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
                var identity = await ctx.ReadSql.ReadOptionalAsync<WorkIdentityData>(sql, ct);
                var type = identity?.Namespace == WorkMetadata.Namespace(MediaType.Movie) ? MediaType.Movie : MediaType.Series;
                if (identity is null || !int.TryParse(identity.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                    || !(await DiscoverQueries.ReadTypesInBatchAsync(ctx, Auth.ProfileId!.Value, ct)).Listed.Contains(type))
                {
                    return (Identity: (WorkIdentityData?)null, Keeps: false, Cached: (BaseDataList<DiscoverItemV1>?)new([]));
                }
                var keeps = await ctx.KeepsDiscoverListsAsync(ct);
                if (keeps)
                {
                    var feed = new ExternalFeedData(identity.Namespace, type, null, null, 1, identity.Value, null);
                    var snapshot = await ExternalFeedCache.ReadAsync(ctx, feed, ct);
                    if (snapshot.Items is { } works)
                    {
                        return (Identity: identity, Keeps: true, Cached: (BaseDataList<DiscoverItemV1>?)await DiscoverQueries.ItemsInBatchAsync(ctx, type, works, Auth.ProfileId!.Value, ct));
                    }
                }
                return (Identity: identity, Keeps: keeps, Cached: (BaseDataList<DiscoverItemV1>?)null);
            }, CancellationToken, mode: SqlAccessMode.ReadOnly);
            if (local.Cached is not null)
            {
                return local.Cached;
            }
            var source = local.Identity!;
            var mediaType = source.Namespace == WorkMetadata.Namespace(MediaType.Movie) ? MediaType.Movie : MediaType.Series;
            var tmdbId = int.Parse(source.Value, CultureInfo.InvariantCulture);
            if (local.Keeps)
            {
                var feed = new ExternalFeedData(source.Namespace, mediaType, null, null, 1, source.Value, null);
                var works = await _feeds.LoadAsync(Context, feed, (entry, ct) => _metadata.FetchExternalFeedAsync(Context, entry, ct), CancellationToken);
                return await DiscoverQueries.ItemsAsync(Context, mediaType, works, Auth.ProfileId!.Value, CancellationToken);
            }

            var connection = await _metadata.ConnectAsync(Context, CancellationToken);
            IReadOnlyList<TmdbSearchResult> results;
            try
            {
                results = await _metadata.ReadRecommendationsAsync(connection, mediaType, tmdbId, CancellationToken);
            }
            catch (KeyNotFoundException)
            {
                // A title TMDB no longer knows has no suggestions.
                results = [];
            }

            // Without kept lists the suggestions still become Works, so each card opens one; a series is TheTVDB's.
            var list = new ExternalWorkListData(ExternalFeedLogic.CatalogOf(source.Namespace), mediaType, null);
            var kept = await MetadataCatalog.KeepWorksAsync(Context, list, await _metadata.ExternalWorksAsync(Context, connection, mediaType, results, CancellationToken), CancellationToken);
            return await DiscoverQueries.ItemsAsync(Context, mediaType, kept, Auth.ProfileId!.Value, CancellationToken);
        }
    }

    // The scopes of the profile's listed media types.
    [ServiceEndpoint("GET", "/api/v1/discover/scopes")]
    public sealed class ReadDiscoverScopesV1 : UserService<DiscoverParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<DiscoverScopeV1>>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(DiscoverParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead() => new BaseDataList<DiscoverScopeV1>(await DiscoverQueries.ReadScopesAsync(Context, Auth.ProfileId!.Value, CancellationToken));
    }

    // The start page's shelves of whole catalogs, all in one statement; scopes of other catalogs keep their own feeds.
    [ServiceEndpoint("POST", "/api/v1/discover/shelves")]
    public sealed class ReadDiscoverShelvesV1 : UserService<DiscoverParametersV1, ReadDiscoverShelvesDataV1>
    {
        private readonly TimeProvider _time;

        internal ReadDiscoverShelvesV1(TimeProvider time)
        {
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<DiscoverShelfV1>>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(DiscoverParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // One local read batch retains the central access check without repeating it for each part.
            return await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var whole = await ctx.ReadWholeExternalWorkCatalogsAsync(ct);
                if (whole.Count == 0) return new BaseDataList<DiscoverShelfV1>([]);

                var scopes = await DiscoverQueries.ReadScopesAsync(ctx, Auth.ProfileId!.Value, ct);
                var shelves = scopes.Where(scope => (Data.ScopeSlug is null || scope.Slug == Data.ScopeSlug) && whole.Contains(DiscoverScopes.Namespace(scope)))
                    .SelectMany(scope => scope.Feeds.Select(feed => (Scope: scope, Feed: feed))).ToArray();
                if (shelves.Length == 0) return new BaseDataList<DiscoverShelfV1>([]);

                var query = new ExternalWorkShelfQueryData([.. shelves.Select((shelf, index) => new ExternalWorkShelfData(index, DiscoverScopes.Namespace(shelf.Scope), shelf.Scope.MediaTypeId,
                    shelf.Scope.MediaSubtypeId, shelf.Feed))], Auth.ProfileId!.Value, DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime));
                var rows = (await ctx.ReadExternalWorkShelvesAsync(query, ct)).ToLookup(row => row.Shelf);
                return new BaseDataList<DiscoverShelfV1>([.. shelves.Select((shelf, index) => shelf.Scope.IsCatalog
                    ? new DiscoverShelfV1(shelf.Scope.Slug, shelf.Feed, [], [.. rows[index].Select(DiscoverQueries.CatalogItem)])
                    : new DiscoverShelfV1(shelf.Scope.Slug, shelf.Feed, [.. rows[index].Select(row => DiscoverQueries.ToItem(shelf.Scope.MediaTypeId, row))], []))]);
            }, CancellationToken, mode: SqlAccessMode.ReadOnly);
        }
    }

    // Titles while typing, from what Discover keeps; without the Caching module there is nothing to suggest from.
    [ServiceEndpoint("POST", "/api/v1/discover/suggestions")]
    public sealed class SuggestDiscoverV1 : UserService<DiscoverParametersV1, SuggestDiscoverDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<DiscoverSuggestionV1>>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(DiscoverParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var text = Data.Query.Trim();
            if (text.Length < 2 || ExternalWorkLogic.SearchTerms(text) is not { } terms
                || !await Context.ExecuteBatchAsync((ctx, ct) => ctx.KeepsDiscoverListsAsync(ct), CancellationToken, mode: SqlAccessMode.ReadOnly))
            {
                return new BaseDataList<DiscoverSuggestionV1>([]);
            }

            var scopes = (await Context.ExecuteBatchAsync((ctx, ct) => DiscoverQueries.ReadScopesAsync(ctx, Auth.ProfileId!.Value, ct), CancellationToken, mode: SqlAccessMode.ReadOnly))
                .Where(scope => Data.ScopeSlug is null || scope.Slug == Data.ScopeSlug).ToArray();
            if (scopes.Length == 0) return new BaseDataList<DiscoverSuggestionV1>([]);

            var query = new ExternalWorkSuggestionQueryData([.. scopes.Select((scope, index) => new ExternalWorkShelfData(index, DiscoverScopes.Namespace(scope), scope.MediaTypeId,
                scope.MediaSubtypeId))], Auth.ProfileId!.Value, terms, ExternalWorkLogic.TitlePrefix(text), ExternalWorkLogic.SuggestionCount);
            var rows = await Context.ExecuteBatchAsync((ctx, ct) => ctx.SuggestExternalWorksAsync(query, ct), CancellationToken, mode: SqlAccessMode.ReadOnly);
            return new BaseDataList<DiscoverSuggestionV1>([.. rows.Select(row => new DiscoverSuggestionV1(scopes[row.Shelf].Slug, row.Namespace, row.Value, row.Title, row.ReleaseYear,
                row.ImageUrl, row.WorkId))]);
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
            // Every result is kept as the Work it is.
            await DiscoverQueries.RequireCatalogTypeAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Auth.ProfileId!.Value, CancellationToken);
            var works = Data.MediaSubtypeId == MediaSubtype.Anime
                ? await MetadataCatalog.KeepAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, await _catalog.SearchAnimeAsync(Data.Query, CancellationToken), CancellationToken)
                : Data.MediaTypeId == MediaType.Series
                    ? await _catalog.SearchAndKeepSeriesAsync(Context, Data.MediaSubtypeId, Data.Query, CancellationToken)
                    : await MetadataCatalog.KeepAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId,
                        await _catalog.SearchAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Data.Query, null, CancellationToken), CancellationToken);
            return await DiscoverQueries.CatalogItemsAsync(Context, works, Auth.ProfileId!.Value, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/discover/catalog/feed")]
    public sealed class ReadDiscoverCatalogFeedV1 : UserService<DiscoverParametersV1, ReadDiscoverCatalogFeedDataV1>
    {
        private readonly MetadataCatalog _catalog;
        private readonly TimeProvider _time;

        internal ReadDiscoverCatalogFeedV1(MetadataCatalog catalog, TimeProvider time)
        {
            _catalog = catalog;
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<DiscoverCatalogItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(DiscoverParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var cached = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await DiscoverQueries.RequireCatalogTypeInBatchAsync(ctx, Data.MediaTypeId, Data.MediaSubtypeId, Auth.ProfileId!.Value, ct);
                var scope = DiscoverScopes.All.FirstOrDefault(scope => scope.MediaTypeId == Data.MediaTypeId && scope.MediaSubtypeId == Data.MediaSubtypeId);
                if (scope is not null && scope.Feeds.Contains(Data.DiscoverFeedTypeId) && (await ctx.ReadWholeExternalWorkCatalogsAsync(ct)).Contains(DiscoverScopes.Namespace(scope)))
                {
                    var shelf = new ExternalWorkShelfData(0, DiscoverScopes.Namespace(scope), Data.MediaTypeId, Data.MediaSubtypeId, Data.DiscoverFeedTypeId);
                    var rows = await ctx.ReadExternalWorkShelvesAsync(new ExternalWorkShelfQueryData([shelf], Auth.ProfileId!.Value,
                        DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime)), ct);
                    return new BaseDataList<DiscoverCatalogItemV1>([.. rows.Select(DiscoverQueries.CatalogItem)]);
                }
                var items = await _catalog.ReadCachedFeedInBatchAsync(ctx, Data.MediaTypeId, Data.MediaSubtypeId, Data.DiscoverFeedTypeId, ct);
                return items is null ? null : await DiscoverQueries.CatalogItemsInBatchAsync(ctx, items, Auth.ProfileId!.Value, ct);
            }, CancellationToken, mode: SqlAccessMode.ReadOnly);
            if (cached is not null)
            {
                return cached;
            }

            var works = await _catalog.ReadFeedAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Data.DiscoverFeedTypeId, CancellationToken);
            return await DiscoverQueries.CatalogItemsAsync(Context, works, Auth.ProfileId!.Value, CancellationToken);
        }
    }

    internal static class DiscoverQueries
    {
        // The scopes of the profile's listed media types.
        public static async Task<IReadOnlyList<DiscoverScopeV1>> ReadScopesAsync(LogicContext context, long profileId, CancellationToken ct)
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
                    AND NOT category."IsHidden";
                """;
            sql.Parameters.Add(new ProfileParameters(profileId));
            var listed = (await context.ReadSql.ReadListAsync<ProfileMediaV1>(sql, ct)).Select(category => category.Key).ToHashSet(StringComparer.Ordinal);
            return [.. DiscoverScopes.All.Where(scope => listed.Contains(scope.MediaTypeId.ToString())
                && (scope.MediaSubtypeId is not { } subtype || listed.Contains(subtype.ToString())))];
        }

        public static DiscoverItemV1 ToItem(MediaType mediaType, ExternalWorkShelfItemData work) => new(mediaType, TmdbId(work.Namespace, work.Value), work.WorkId, work.Title,
            work.ReleaseYear, work.Overview, work.ImageUrl, work.BackdropUrl, work.MediaStateTypeId ?? MediaStateType.NotRequested, work.PlexUrl, work.Rating);

        // A card names TMDB's id only for a title of TMDB's catalog.
        private static int? TmdbId(string @namespace, string value) =>
            @namespace is "tmdb_movie" or "tmdb_tv" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

        public static DiscoverCatalogItemV1 CatalogItem(ExternalWorkShelfItemData work) => new(work.Namespace, work.Value, work.Title, work.ReleaseYear, work.Creator, work.WorkId, work.ImageUrl, work.Rating,
            work.MediaStateTypeId ?? MediaStateType.NotRequested);

        // Films and series are discovered through TMDB's lists, series as TheTVDB's; anime series and the other types through their catalogs.
        public static Task RequireCatalogTypeAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, long profileId, CancellationToken ct) =>
            context.ExecuteBatchAsync(async (ctx, token) =>
            {
                await RequireCatalogTypeInBatchAsync(ctx, mediaType, subtype, profileId, token);
                return true;
            }, ct, mode: SqlAccessMode.ReadOnly);

        public static async Task RequireCatalogTypeInBatchAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, long profileId, CancellationToken ct)
        {
            var anime = mediaType == MediaType.Series && subtype == MediaSubtype.Anime;
            if (!DiscoverScopes.IsCatalog(mediaType, subtype))
            {
                throw new DataValidationException("MediaTypeId", "unknown_catalog_value");
            }

            if (!(await ReadTypesInBatchAsync(context, profileId, ct)).Usable.Contains(mediaType))
            {
                throw new KeyNotFoundException("The media type is not available.");
            }
            if (anime)
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        category."Key",
                        TRUE AS "IsSubtype",
                        category."IsHidden"
                    FROM
                        "ProfileMediaCategories" AS category
                    WHERE
                        category."ProfileId" = @ProfileId
                        AND category."MediaSubtypeId" = 1;
                    """;
                sql.Parameters.Add(new ProfileParameters(profileId));
                if ((await context.ReadSql.ReadListAsync<ProfileMediaV1>(sql, ct)).Count == 0)
                {
                    throw new KeyNotFoundException("The media type is not available.");
                }
            }
        }

        public static Task<(IReadOnlySet<MediaType> Usable, IReadOnlySet<MediaType> Listed)> ReadTypesAsync(LogicContext context, long profileId, CancellationToken ct) =>
            context.ExecuteBatchAsync((ctx, token) => ReadTypesInBatchAsync(ctx, profileId, token), ct, mode: SqlAccessMode.ReadOnly);

        public static async Task<(IReadOnlySet<MediaType> Usable, IReadOnlySet<MediaType> Listed)> ReadTypesInBatchAsync(LogicContext context, long profileId, CancellationToken ct)
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
            var categories = await context.ReadSql.ReadListAsync<ProfileMediaV1>(sql, ct);
            return (categories.Select(category => Enum.Parse<MediaType>(category.Key)).ToHashSet(),
                categories.Where(category => !category.IsHidden).Select(category => Enum.Parse<MediaType>(category.Key)).ToHashSet());
        }

        // The cards of the Works a list names, each with the profile's state of it.
        public static Task<BaseDataList<DiscoverItemV1>> ItemsAsync(LogicContext context, MediaType mediaType, IReadOnlyList<ExternalWorkData> works, long profileId, CancellationToken ct) =>
            context.ExecuteBatchAsync((ctx, token) => ItemsInBatchAsync(ctx, mediaType, works, profileId, token), ct, mode: SqlAccessMode.ReadOnly);

        public static async Task<BaseDataList<DiscoverItemV1>> ItemsInBatchAsync(LogicContext context, MediaType mediaType, IReadOnlyList<ExternalWorkData> works, long profileId, CancellationToken ct)
        {
            var ids = WorkIds(works);
            var states = await ReadStatesInBatchAsync(context, ids, profileId, ct);
            return new BaseDataList<DiscoverItemV1>([.. works.Select((work, index) =>
            {
                var state = states.GetValueOrDefault(ids[index]);
                return new DiscoverItemV1(mediaType, TmdbId(work.Namespace, work.Value), ids[index], work.Title, work.ReleaseYear, work.Overview, state?.PosterUrl ?? work.ImageUrl,
                    state?.BackdropUrl ?? work.BackdropUrl, state?.MediaStateTypeId ?? MediaStateType.NotRequested, state?.PlexUrl, work.Rating);
            })]);
        }

        public static Task<BaseDataList<DiscoverCatalogItemV1>> CatalogItemsAsync(LogicContext context, IReadOnlyList<ExternalWorkData> works, long profileId, CancellationToken ct) =>
            context.ExecuteBatchAsync((ctx, token) => CatalogItemsInBatchAsync(ctx, works, profileId, token), ct, mode: SqlAccessMode.ReadOnly);

        public static async Task<BaseDataList<DiscoverCatalogItemV1>> CatalogItemsInBatchAsync(LogicContext context, IReadOnlyList<ExternalWorkData> works, long profileId, CancellationToken ct)
        {
            var ids = WorkIds(works);
            var states = await ReadStatesInBatchAsync(context, ids, profileId, ct);
            return new BaseDataList<DiscoverCatalogItemV1>([.. works.Select((work, index) =>
            {
                var state = states.GetValueOrDefault(ids[index]);
                return new DiscoverCatalogItemV1(work.Namespace, work.Value, work.Title, work.ReleaseYear, work.Creator, ids[index], state?.PosterUrl ?? work.ImageUrl, work.Rating,
                    state?.MediaStateTypeId ?? MediaStateType.NotRequested, work.SeasonNumber);
            })]);
        }

        // Every title of a kept or saved list is a Work.
        private static long[] WorkIds(IReadOnlyList<ExternalWorkData> works) =>
            [.. works.Select(work => work.WorkId ?? throw new InvalidOperationException("A Discover title is no Work."))];

        // The profile's state of the Works it may see, with the images Jularr keeps for them; the provider's own stay in use until then.
        private static async Task<IReadOnlyDictionary<long, DiscoverCardStateData>> ReadStatesInBatchAsync(LogicContext context, long[] workIds, long profileId, CancellationToken ct)
        {
            if (workIds.Length == 0) return new Dictionary<long, DiscoverCardStateData>();

            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    visible."WorkId",
                    state."MediaStateTypeId",
                    (
                        SELECT
                            'https://app.plex.tv/desktop/#!/server/' || provider."ExternalId" || '/details?key=%2Flibrary%2Fmetadata%2F' || plex."Value"
                        FROM
                            "WorkExternalIdentities" AS plex
                            JOIN "Providers" AS provider ON provider."Key" = 'plex' AND provider."IsEnabled" AND provider."ExternalId" IS NOT NULL
                        WHERE
                            plex."WorkId" = visible."WorkId"
                            AND plex."Namespace" = 'plex'
                        LIMIT 1
                    ) AS "PlexUrl",
                    (
                        SELECT
                            fact."Value"
                        FROM
                            "WorkMetadataFacts" AS fact
                        WHERE
                            fact."WorkId" = visible."WorkId"
                            AND fact."FactKey" = 'Poster'
                            AND fact."Value" LIKE '/api/v1/covers/%'
                        ORDER BY
                            fact."IsManual" DESC
                        LIMIT 1
                    ) AS "PosterUrl",
                    (
                        SELECT
                            fact."Value"
                        FROM
                            "WorkMetadataFacts" AS fact
                        WHERE
                            fact."WorkId" = visible."WorkId"
                            AND fact."FactKey" = 'Backdrop'
                            AND fact."Value" LIKE '/api/v1/covers/%'
                        ORDER BY
                            fact."IsManual" DESC
                        LIMIT 1
                    ) AS "BackdropUrl"
                FROM
                    "ProfileWorks" AS visible
                    JOIN "ProfileWorkStates" AS state ON state."ProfileId" = visible."ProfileId" AND state."WorkId" = visible."WorkId"
                WHERE
                    visible."ProfileId" = @ProfileId
                    AND visible."WorkId" = ANY(@WorkIds);
                """;
            sql.Parameters.Add(new DiscoverWorksData([.. workIds.Distinct()]));
            sql.Parameters.Add(new ProfileParameters(profileId));
            return (await context.ReadSql.ReadListAsync<DiscoverCardStateData>(sql, ct)).ToDictionary(row => row.WorkId);
        }
    }
}
