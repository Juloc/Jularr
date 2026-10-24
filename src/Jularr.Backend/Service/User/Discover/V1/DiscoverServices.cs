using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Data.User.Request.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Tmdb;
using Jularr.Integrations.Logic.Tmdb;
using UserMedia = Jularr.Backend.Data.User.Media.V1;

namespace Jularr.Backend.Service.User.Discover.V1
{
    [ServiceEndpoint("POST", "/api/v1/discover/feed")]
    public sealed class ReadDiscoverFeedV1 : UserService<DiscoverParametersV1, ReadDiscoverFeedDataV1>
    {
        private readonly WorkMetadata _metadata;
        private readonly TimeProvider _time;

        internal ReadDiscoverFeedV1(WorkMetadata metadata, TimeProvider time)
        {
            _metadata = metadata;
            _time = time;
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

            // A whole catalog answers every page itself.
            var list = new ExternalWorkListData(WorkMetadata.Namespace(Data.MediaTypeId), Data.MediaTypeId, null);
            if ((await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadWholeExternalWorkCatalogsAsync(ct), CancellationToken, mode: SqlAccessMode.ReadOnly)).Contains(list.Namespace))
            {
                var shelf = new ExternalWorkShelfData(0, list.Namespace, Data.MediaTypeId, null, Data.DiscoverFeedTypeId);
                var rows = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadExternalWorkShelvesAsync(new ExternalWorkShelfQueryData([shelf], Auth.ProfileId!.Value,
                    DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime), (Data.Page - 1) * ExternalWorkLogic.ShelfSize), ct), CancellationToken, mode: SqlAccessMode.ReadOnly);
                return new BaseDataList<DiscoverItemV1>([.. rows.Select(row => DiscoverQueries.ToItem(Data.MediaTypeId, row))]);
            }

            // The first trending page comes from the catalog while it is fresh, and from there too while TMDB cannot answer.
            var keeps = await Context.ExecuteBatchAsync((ctx, ct) => ctx.KeepsDiscoverTitlesAsync(ct), CancellationToken, mode: SqlAccessMode.ReadOnly);
            var isTrending = Data.DiscoverFeedTypeId == DiscoverFeedType.Trending && Data.Page <= 1;
            var known = keeps && isTrending ? await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadTrendingExternalWorksAsync(list, ct), CancellationToken, mode: SqlAccessMode.ReadOnly) : [];
            IReadOnlyList<ExternalWorkData> works;
            if (ExternalWorkLogic.IsFresh(known, _time.GetUtcNow()))
            {
                works = known;
            }
            else
            {
                try
                {
                    var connection = await _metadata.ConnectAsync(Context, CancellationToken);
                    var results = await _metadata.ReadFeedAsync(connection, Data.MediaTypeId, Data.DiscoverFeedTypeId, Data.Page, CancellationToken);
                    works = [.. results.Select(result => DiscoverQueries.ExternalWork(list.Namespace, result))];
                    if (keeps) await Context.ExecuteBatchAsync((ctx, ct) => ctx.SaveExternalWorksAsync(new ExternalWorkBatchData(list, works, isTrending), ct), CancellationToken);
                }
                catch (ServiceUnavailableException) when (known.Count > 0)
                {
                    works = known;
                }
            }

            var overlay = await DiscoverQueries.ReadOverlayAsync(Context, Data.MediaTypeId, works.Select(work => int.Parse(work.Value, CultureInfo.InvariantCulture)), Auth.ProfileId!.Value, CancellationToken);
            return new BaseDataList<DiscoverItemV1>(works.Select(work => DiscoverQueries.ToItem(Data.MediaTypeId, work, overlay)).ToArray());
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
                        (
                            SELECT
                                catalogEntry."Rating"
                            FROM
                                "WorkExternalIdentities" AS identity
                                JOIN "ExternalWorks" AS catalogEntry ON catalogEntry."Namespace" = identity."Namespace" AND catalogEntry."Value" = identity."Value"
                            WHERE
                                identity."WorkId" = work."Id"
                                AND catalogEntry."Rating" IS NOT NULL
                            ORDER BY
                                catalogEntry."Votes" DESC NULLS LAST
                            LIMIT 1
                        ) AS "Rating"
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
                    var list = new ExternalWorkListData(WorkMetadata.Namespace(mediaType), mediaType, null);
                    if (await Context.ExecuteBatchAsync((ctx, ct) => ctx.KeepsDiscoverTitlesAsync(ct), CancellationToken, mode: SqlAccessMode.ReadOnly))
                    {
                        await Context.ExecuteBatchAsync((ctx, ct) => ctx.SaveExternalWorksAsync(new ExternalWorkBatchData(list, [.. results.Select(result => DiscoverQueries.ExternalWork(list.Namespace, result))], false), ct),
                            CancellationToken);
                    }
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
            return DiscoverQueries.ToItem(Parameters.MediaTypeId, result, overlay, work.Details.TrailerKey);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/discover/{mediaTypeId}/{tmdbId:int}/requests")]
    public sealed class RequestDiscoverItemV1 : UserService<DiscoverItemParametersV1, CreateRequestDataV1>
    {
        private readonly WorkMetadata _metadata;
        private readonly AcquisitionSearchSignal _searchSignal;
        private bool _newlyApproved;

        internal RequestDiscoverItemV1(WorkMetadata metadata, AcquisitionSearchSignal searchSignal)
        {
            _metadata = metadata;
            _searchSignal = searchSignal;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateRequestResultV1>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverItemParametersV1 parameters) => ModuleRequirement.All("Library", "Requests");
        protected override ServicePermission GetPermission(DiscoverItemParametersV1 parameters, Type resultType) => new("profile.requests.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The provider identity resolves to one canonical Work before the request exists.
            await DiscoverQueries.RequireTypeAsync(Context, Parameters.MediaTypeId, Auth.ProfileId!.Value, CancellationToken);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.RequireNotExcludedAsync(WorkMetadata.Namespace(Parameters.MediaTypeId), Parameters.TmdbId.ToString(global::System.Globalization.CultureInfo.InvariantCulture), ct);
                return true;
            }, CancellationToken);
            var connection = await _metadata.ConnectAsync(Context, CancellationToken);
            var workId = await _metadata.ImportAsync(Context, connection, Parameters.MediaTypeId, null, Parameters.TmdbId, CancellationToken);
            var (request, newlyApproved) = await Context.ExecuteBatchAsync((ctx, ct) => ctx.CreateAcquisitionRequestAsync(new AcquisitionRequestCreationData(workId, Auth.ProfileId!.Value, Auth.AccountId!.Value, Data.UiLocaleId, null, null, Auth.AccountRoleTypeId == AccountRoleType.Owner), ct),
                CancellationToken);
            _newlyApproved = newlyApproved;
            return new CreateRequestResultV1(request.Id);
        }

        protected override void OnCommitted()
        {
            if (_newlyApproved) _searchSignal.RequestSearch();
        }
    }

    [ServiceEndpoint("POST", "/api/v1/works/{workId:long}/recommendations")]
    public sealed class ReadWorkRecommendationsV1 : UserService<UserMedia.WorkParametersV1, NoData>
    {
        private readonly WorkMetadata _metadata;

        internal ReadWorkRecommendationsV1(WorkMetadata metadata)
        {
            _metadata = metadata;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<DiscoverItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(UserMedia.WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(UserMedia.WorkParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // TMDB's recommendations for a film or series the profile sees, in the profile's types; other Works have none.
            var identity = await Context.ExecuteBatchAsync((ctx, ct) =>
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
                return ctx.ReadSql.ReadOptionalAsync<WorkIdentityData>(sql, ct);
            }, CancellationToken, mode: SqlAccessMode.ReadOnly);
            var mediaType = identity?.Namespace == WorkMetadata.Namespace(MediaType.Movie) ? MediaType.Movie : MediaType.Series;
            if (identity is null || !int.TryParse(identity.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var tmdbId)
                || !(await DiscoverQueries.ReadTypesAsync(Context, Auth.ProfileId!.Value, CancellationToken)).Listed.Contains(mediaType))
            {
                return new BaseDataList<DiscoverItemV1>([]);
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

            var overlay = await DiscoverQueries.ReadOverlayAsync(Context, mediaType, results.Select(result => result.Id), Auth.ProfileId!.Value, CancellationToken);
            return new BaseDataList<DiscoverItemV1>([.. results.Select(result => DiscoverQueries.ToItem(mediaType, result, overlay))]);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/discover/catalog/entry")]
    public sealed class ReadDiscoverCatalogEntryV1 : UserService<DiscoverParametersV1, ReadDiscoverCatalogEntryDataV1>
    {
        private readonly MetadataCatalog _catalog;

        internal ReadDiscoverCatalogEntryV1(MetadataCatalog catalog)
        {
            _catalog = catalog;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DiscoverCatalogEntryV1>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(DiscoverParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            await DiscoverQueries.RequireCatalogTypeAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Auth.ProfileId!.Value, CancellationToken);
            return await _catalog.ReadEntryAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Data.Namespace, Data.ExternalId, CancellationToken);
        }
    }

    // The description of a catalog title from what the local catalog keeps of it; no provider is asked.
    [ServiceEndpoint("GET", "/api/v1/discover/preview")]
    public sealed class ReadDiscoverPreviewV1 : UserService<ReadDiscoverPreviewParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DiscoverPreviewV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadDiscoverPreviewParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadDiscoverPreviewParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Title",
                    work."Overview",
                    work."ImageUrl" AS "PosterUrl",
                    work."BackdropUrl",
                    work."ReleaseYear"::integer AS "ReleaseYear"
                FROM
                    "ExternalWorks" AS work
                WHERE
                    work."Namespace" = @Namespace
                    AND work."Value" = @ExternalId
                    AND NOT work."IsAdult";
                """;
            sql.Parameters.Add(Parameters);
            return await Context.ReadSql.ReadRequiredAsync<DiscoverPreviewV1>(sql, CancellationToken);
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
            var whole = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadWholeExternalWorkCatalogsAsync(ct), CancellationToken, mode: SqlAccessMode.ReadOnly);
            if (whole.Count == 0) return new BaseDataList<DiscoverShelfV1>([]);

            var scopes = await Context.ExecuteBatchAsync((ctx, ct) => DiscoverQueries.ReadScopesAsync(ctx, Auth.ProfileId!.Value, ct), CancellationToken, mode: SqlAccessMode.ReadOnly);
            var shelves = scopes.Where(scope => (Data.ScopeSlug is null || scope.Slug == Data.ScopeSlug) && whole.Contains(DiscoverQueries.Namespace(scope)))
                .SelectMany(scope => scope.Feeds.Select(feed => (Scope: scope, Feed: feed))).ToArray();
            if (shelves.Length == 0) return new BaseDataList<DiscoverShelfV1>([]);

            var query = new ExternalWorkShelfQueryData([.. shelves.Select((shelf, index) => new ExternalWorkShelfData(index, DiscoverQueries.Namespace(shelf.Scope), shelf.Scope.MediaTypeId,
                shelf.Scope.MediaSubtypeId, shelf.Feed))], Auth.ProfileId!.Value, DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime));
            var rows = (await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadExternalWorkShelvesAsync(query, ct), CancellationToken, mode: SqlAccessMode.ReadOnly)).ToLookup(row => row.Shelf);
            return new BaseDataList<DiscoverShelfV1>([.. shelves.Select((shelf, index) => shelf.Scope.IsCatalog
                ? new DiscoverShelfV1(shelf.Scope.Slug, shelf.Feed, [], [.. rows[index].Select(DiscoverQueries.CatalogItem)])
                : new DiscoverShelfV1(shelf.Scope.Slug, shelf.Feed, [.. rows[index].Select(row => DiscoverQueries.ToItem(shelf.Scope.MediaTypeId, row))], []))]);
        }
    }

    // Titles while typing, from what Discover keeps; without DiscoverCache there is nothing to suggest from.
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
                || !await Context.ExecuteBatchAsync((ctx, ct) => ctx.KeepsDiscoverTitlesAsync(ct), CancellationToken, mode: SqlAccessMode.ReadOnly))
            {
                return new BaseDataList<DiscoverSuggestionV1>([]);
            }

            var scopes = (await Context.ExecuteBatchAsync((ctx, ct) => DiscoverQueries.ReadScopesAsync(ctx, Auth.ProfileId!.Value, ct), CancellationToken, mode: SqlAccessMode.ReadOnly))
                .Where(scope => Data.ScopeSlug is null || scope.Slug == Data.ScopeSlug).ToArray();
            if (scopes.Length == 0) return new BaseDataList<DiscoverSuggestionV1>([]);

            var query = new ExternalWorkSuggestionQueryData([.. scopes.Select((scope, index) => new ExternalWorkShelfData(index, DiscoverQueries.Namespace(scope), scope.MediaTypeId,
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
            await DiscoverQueries.RequireCatalogTypeAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Auth.ProfileId!.Value, CancellationToken);
            var results = Data.MediaSubtypeId == MediaSubtype.Anime ? await _catalog.SearchAnimeAsync(Context, Data.Query, CancellationToken)
                : await _catalog.SearchAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Data.Query, null, CancellationToken);
            await _catalog.KeepAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, results, CancellationToken);
            return new BaseDataList<DiscoverCatalogItemV1>([.. results.Select(DiscoverQueries.CatalogItem)]);
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
            await DiscoverQueries.RequireCatalogTypeAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Auth.ProfileId!.Value, CancellationToken);
            var scope = DiscoverScopes.All.FirstOrDefault(scope => scope.MediaTypeId == Data.MediaTypeId && scope.MediaSubtypeId == Data.MediaSubtypeId);
            if (scope is not null && scope.Feeds.Contains(Data.DiscoverFeedTypeId)
                && (await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadWholeExternalWorkCatalogsAsync(ct), CancellationToken, mode: SqlAccessMode.ReadOnly)).Contains(DiscoverQueries.Namespace(scope)))
            {
                var shelf = new ExternalWorkShelfData(0, DiscoverQueries.Namespace(scope), Data.MediaTypeId, Data.MediaSubtypeId, Data.DiscoverFeedTypeId);
                var rows = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadExternalWorkShelvesAsync(new ExternalWorkShelfQueryData([shelf], Auth.ProfileId!.Value,
                    DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime)), ct), CancellationToken, mode: SqlAccessMode.ReadOnly);
                return new BaseDataList<DiscoverCatalogItemV1>([.. rows.Select(DiscoverQueries.CatalogItem)]);
            }

            var results = await _catalog.ReadFeedAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Data.DiscoverFeedTypeId, CancellationToken);
            return new BaseDataList<DiscoverCatalogItemV1>([.. results.Select(DiscoverQueries.CatalogItem)]);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/discover/catalog/requests")]
    public sealed class RequestDiscoverCatalogItemV1 : UserService<DiscoverParametersV1, RequestDiscoverCatalogDataV1>
    {
        private readonly MetadataCatalog _catalog;
        private readonly AcquisitionSearchSignal _searchSignal;
        private bool _newlyApproved;

        internal RequestDiscoverCatalogItemV1(MetadataCatalog catalog, AcquisitionSearchSignal searchSignal)
        {
            _catalog = catalog;
            _searchSignal = searchSignal;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateRequestResultV1>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverParametersV1 parameters) => ModuleRequirement.All("Library", "Requests");
        protected override ServicePermission GetPermission(DiscoverParametersV1 parameters, Type resultType) => new("profile.requests.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The catalog identity resolves to one canonical Work before the request exists.
            await DiscoverQueries.RequireCatalogTypeAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Auth.ProfileId!.Value, CancellationToken);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.RequireNotExcludedAsync(Data.Namespace, Data.ExternalId, ct);
                return true;
            }, CancellationToken);
            var workId = await _catalog.ImportAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Data.Namespace, Data.ExternalId, CancellationToken);
            var (request, newlyApproved) = await Context.ExecuteBatchAsync((ctx, ct) => ctx.CreateAcquisitionRequestAsync(new AcquisitionRequestCreationData(workId, Auth.ProfileId!.Value, Auth.AccountId!.Value,
                Data.UiLocaleId, null, null, Auth.AccountRoleTypeId == AccountRoleType.Owner), ct), CancellationToken);
            _newlyApproved = newlyApproved;
            return new CreateRequestResultV1(request.Id);
        }

        protected override void OnCommitted()
        {
            if (_newlyApproved) _searchSignal.RequestSearch();
        }
    }

    internal static class DiscoverQueries
    {
        // The catalog that keeps a scope's titles.
        public static string Namespace(DiscoverScopeV1 scope) => scope.MediaSubtypeId is MediaSubtype.Anime or MediaSubtype.Manga or MediaSubtype.LightNovel
            ? AniListMetadata.Namespace
            : scope.MediaTypeId switch
            {
                MediaType.Movie or MediaType.Series => WorkMetadata.Namespace(scope.MediaTypeId),
                MediaType.Book => BookMetadata.Namespace,
                MediaType.Music => MusicMetadata.Namespace,
                _ => GameMetadata.Namespace
            };

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

        public static DiscoverItemV1 ToItem(MediaType mediaType, ExternalWorkShelfItemData work) => new(mediaType, int.Parse(work.Value, CultureInfo.InvariantCulture), work.WorkId, work.Title,
            work.ReleaseYear, work.Overview, work.ImageUrl, work.BackdropUrl, work.MediaStateTypeId ?? MediaStateType.NotRequested, work.PlexUrl, work.Rating);

        public static DiscoverCatalogItemV1 CatalogItem(ExternalWorkShelfItemData work) => new(work.Namespace, work.Value, work.Title, work.ReleaseYear, work.Creator, work.WorkId, work.ImageUrl, work.Rating, work.MediaStateTypeId);

        public static DiscoverCatalogItemV1 CatalogItem(MetadataSearchResultV1 result) =>
            new(result.Namespace, result.ExternalId, result.Title, result.ReleaseYear, result.Author, result.WorkId, result.PosterUrl, null, null);

        // Films and series are discovered through TMDB; anime series and the other types through their catalogs.
        public static async Task RequireCatalogTypeAsync(LogicContext context, MediaType mediaType, MediaSubtype? subtype, long profileId, CancellationToken ct)
        {
            var anime = mediaType == MediaType.Series && subtype == MediaSubtype.Anime;
            if (!DiscoverScopes.IsCatalog(mediaType, subtype))
            {
                throw new DataValidationException("MediaTypeId", "unknown_catalog_value");
            }

            await RequireTypeAsync(context, mediaType, profileId, ct);
            if (anime && (await context.ExecuteBatchAsync((ctx, token) =>
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
                return ctx.ReadSql.ReadListAsync<ProfileMediaV1>(sql, token);
            }, ct, mode: SqlAccessMode.ReadOnly)).Count == 0)
            {
                throw new KeyNotFoundException("The media type is not available.");
            }
        }

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
            }, ct, mode: SqlAccessMode.ReadOnly);
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
                                fact."Value"
                            FROM
                                "WorkMetadataFacts" AS fact
                            WHERE
                                fact."WorkId" = identity."WorkId"
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
                                fact."WorkId" = identity."WorkId"
                                AND fact."FactKey" = 'Backdrop'
                                AND fact."Value" LIKE '/api/v1/covers/%'
                            ORDER BY
                                fact."IsManual" DESC
                            LIMIT 1
                        ) AS "BackdropUrl",
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
            }, ct, mode: SqlAccessMode.ReadOnly);
            return rows.ToDictionary(row => int.Parse(row.Value, CultureInfo.InvariantCulture));
        }

        // A TMDB title as the catalog keeps it.
        public static ExternalWorkData ExternalWork(string @namespace, TmdbSearchResult result) => new(@namespace, result.Id.ToString(CultureInfo.InvariantCulture),
            TextLimit.Cut(result.Title, 500)!, TextLimit.Cut(result.OriginalTitle, 500), result.Year is >= 1800 and <= 2200 ? result.Year : null, TextLimit.Cut(result.Overview, 4000), null,
            TmdbClient.PosterUrl(result.PosterPath), TmdbClient.BackdropUrl(result.BackdropPath), DateTime.MinValue);

        public static DiscoverItemV1 ToItem(MediaType mediaType, ExternalWorkData work, IReadOnlyDictionary<int, DiscoverOverlayData> overlay)
        {
            var id = int.Parse(work.Value, CultureInfo.InvariantCulture);
            var known = overlay.GetValueOrDefault(id);
            return new DiscoverItemV1(mediaType, id, known?.WorkId, work.Title, work.ReleaseYear, work.Overview, known?.PosterUrl ?? work.ImageUrl, known?.BackdropUrl ?? work.BackdropUrl,
                known?.MediaStateTypeId ?? MediaStateType.NotRequested, known?.PlexUrl, work.Rating);
        }

        // TMDB's average vote; nothing while nobody voted.
        private static decimal? Rating(double? voteAverage) => voteAverage is > 0 and <= 10 ? Math.Round((decimal)voteAverage.Value, 2) : null;

        public static DiscoverItemV1 ToItem(MediaType mediaType, TmdbSearchResult result, IReadOnlyDictionary<int, DiscoverOverlayData> overlay, string? trailerKey = null)
        {
            var known = overlay.GetValueOrDefault(result.Id);
            return new DiscoverItemV1(mediaType, result.Id, known?.WorkId, TextLimit.Cut(result.Title, 500)!, result.Year is >= 1800 and <= 2200 ? result.Year : null, TextLimit.Cut(result.Overview, 4000),
                known?.PosterUrl ?? TmdbClient.PosterUrl(result.PosterPath), known?.BackdropUrl ?? TmdbClient.BackdropUrl(result.BackdropPath), known?.MediaStateTypeId ?? MediaStateType.NotRequested, known?.PlexUrl, Rating(result.VoteAverage), trailerKey);
        }
    }
}
