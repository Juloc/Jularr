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
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Data.Tmdb;
using Jularr.Integrations.Logic.Tmdb;
using Jularr.Integrations.Logic.Tmdb;
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
                var keeps = await ctx.KeepsDiscoverTitlesAsync(ct);
                if (keeps)
                {
                    var snapshot = await ExternalFeedCache.ReadAsync(ctx, feed, ct);
                    if (snapshot.Items is { } cached)
                    {
                        var known = await DiscoverQueries.ReadOverlayInBatchAsync(ctx, Data.MediaTypeId, cached.Select(work => int.Parse(work.Value, CultureInfo.InvariantCulture)), Auth.ProfileId!.Value, ct);
                        return (Keeps: true, Cached: (BaseDataList<DiscoverItemV1>?)new(cached.Select(work => DiscoverQueries.ToItem(Data.MediaTypeId, work, known)).ToArray()));
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
                var connection = await _metadata.ConnectAsync(Context, CancellationToken);
                var results = await _metadata.ReadFeedAsync(connection, Data.MediaTypeId, Data.DiscoverFeedTypeId, Data.Page, CancellationToken);
                works = [.. results.Select(result => WorkMetadata.ExternalWork(list.Namespace, result))];
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
                        await Context.ExecuteBatchAsync((ctx, ct) => ctx.SaveExternalWorksAsync(new ExternalWorkBatchData(list, [.. results.Select(result => WorkMetadata.ExternalWork(list.Namespace, result))], false), ct),
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
                var keeps = await ctx.KeepsDiscoverTitlesAsync(ct);
                if (keeps)
                {
                    var feed = new ExternalFeedData(identity.Namespace, type, null, null, 1, identity.Value, null);
                    var snapshot = await ExternalFeedCache.ReadAsync(ctx, feed, ct);
                    if (snapshot.Items is { } works)
                    {
                        var known = await DiscoverQueries.ReadOverlayInBatchAsync(ctx, type, works.Select(work => int.Parse(work.Value, CultureInfo.InvariantCulture)), Auth.ProfileId!.Value, ct);
                        return (Identity: identity, Keeps: true, Cached: (BaseDataList<DiscoverItemV1>?)new(works.Select(work => DiscoverQueries.ToItem(type, work, known)).ToArray()));
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
                var known = await DiscoverQueries.ReadOverlayAsync(Context, mediaType, works.Select(work => int.Parse(work.Value, CultureInfo.InvariantCulture)), Auth.ProfileId!.Value, CancellationToken);
                return new BaseDataList<DiscoverItemV1>([.. works.Select(work => DiscoverQueries.ToItem(mediaType, work, known))]);
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

    // Explicitly favoriting an external item imports the canonical Work once; browsing never does.
    [ServiceEndpoint("POST", "/api/v1/discover/catalog/favorite")]
    public sealed class FavoriteDiscoverCatalogItemV1 : UserService<DiscoverParametersV1, ReadDiscoverCatalogEntryDataV1>
    {
        private readonly MetadataCatalog _catalog;
        private readonly AcquisitionSearchSignal _searchSignal;
        private bool _newlyApproved;

        internal FavoriteDiscoverCatalogItemV1(MetadataCatalog catalog, AcquisitionSearchSignal searchSignal)
        {
            _catalog = catalog;
            _searchSignal = searchSignal;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<FavoriteDiscoverResultV1>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(DiscoverParametersV1 parameters, Type resultType) => new("profile.watchlist.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var catalog = Data.Namespace is "anilist" or "openlibrary";
            if (catalog)
                await DiscoverQueries.RequireCatalogTypeAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Auth.ProfileId!.Value, CancellationToken);
            else if (Data.Namespace is "tmdb_movie" or "tmdb_tv" or "tvdb")
                await DiscoverQueries.RequireTypeAsync(Context, Data.MediaTypeId, Auth.ProfileId!.Value, CancellationToken);
            else
                throw new KeyNotFoundException("Unknown external identity.");

            // External lookup and metadata import precede the short atomic profile write.
            var workId = await _catalog.ImportAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Data.Namespace, Data.ExternalId, CancellationToken);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement = """
                    SELECT profile."AutoRequestOnFavorite", profile."UiLocaleId",
                        EXISTS (SELECT 1 FROM "WatchlistEntries" AS existing
                            WHERE existing."ProfileId" = profile."Id" AND existing."WorkId" = @WorkId) AS "AlreadyFavorite",
                        EXISTS (SELECT 1 FROM "InstanceModules" AS module
                            WHERE module."Key" = 'Requests' AND module."IsEnabled") AS "RequestsEnabled"
                    FROM "Profiles" AS profile
                    WHERE profile."Id" = @ProfileId
                    FOR UPDATE OF profile;
                    """;
                sql.Parameters.Add(new { ProfileId = Auth.ProfileId!.Value, WorkId = workId });
                var preference = await ctx.ReadRequiredAsync<FavoriteRequestPreference>(sql, ct);
                await ctx.UpdateWatchlistAsync(new WatchlistData(Auth.ProfileId!.Value, workId), true, ct);

                // Opt-in, only for a NEW favorite while Requests is enabled.
                // Uses the normal quota, moderation, language and Wanted creation rules.
                if (preference.AutoRequestOnFavorite && !preference.AlreadyFavorite && preference.RequestsEnabled)
                {
                    var (_, newlyApproved) = await ctx.CreateAcquisitionRequestAsync(new AcquisitionRequestCreationData(
                        workId, Auth.ProfileId!.Value, Auth.AccountId!.Value, preference.UiLocaleId,
                        null, null, Auth.AccountRoleTypeId == AccountRoleType.Owner), ct);
                    _newlyApproved = newlyApproved;
                }
                return true;
            }, CancellationToken);
            return new FavoriteDiscoverResultV1(workId);
        }

        protected override void OnCommitted()
        {
            if (_newlyApproved) _searchSignal.RequestSearch();
        }

        private sealed record FavoriteRequestPreference(bool AutoRequestOnFavorite, long UiLocaleId, bool AlreadyFavorite, bool RequestsEnabled);
    }

    // A read-only, provider-backed snapshot for the regular Details Razor page.
    // Resolving it NEVER persists a Work, WatchlistEntry, Request or WantedItem.
    [ServiceEndpoint("POST", "/api/v1/discover/external-details")]
    public sealed class ReadDiscoverExternalDetailsV1 : UserService<DiscoverParametersV1, ReadDiscoverCatalogEntryDataV1>
    {
        private readonly MetadataCatalog _catalog;
        private readonly WorkMetadata _metadata;
        private readonly TvdbMetadata _tvdb;
        private readonly AniBridgeMappings _bridge;

        internal ReadDiscoverExternalDetailsV1(MetadataCatalog catalog, WorkMetadata metadata, TvdbMetadata tvdb, AniBridgeMappings bridge)
        {
            _catalog = catalog;
            _metadata = metadata;
            _tvdb = tvdb;
            _bridge = bridge;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DiscoverExternalDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(DiscoverParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var source = Data.Namespace;
            var anime = source == "anilist" && Data.MediaSubtypeId == MediaSubtype.Anime;
            var catalog = source == "openlibrary" && Data.MediaTypeId == MediaType.Book
                || source == "anilist" && (Data.MediaTypeId == MediaType.Book
                    && Data.MediaSubtypeId is MediaSubtype.Manga or MediaSubtype.LightNovel
                    || anime && Data.MediaTypeId is MediaType.Series or MediaType.Movie);
            var tmdb = source == "tmdb_movie" && Data.MediaTypeId == MediaType.Movie
                || source == "tmdb_tv" && Data.MediaTypeId == MediaType.Series;
            var tvdb = source == "tvdb" && Data.MediaTypeId == MediaType.Series;
            if (!catalog && !tmdb && !tvdb) throw new KeyNotFoundException("Unknown external detail identity.");

            if (catalog)
                await DiscoverQueries.RequireCatalogTypeAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, Auth.ProfileId!.Value, CancellationToken);
            else
                await DiscoverQueries.RequireTypeAsync(Context, Data.MediaTypeId, Auth.ProfileId!.Value, CancellationToken);

            DiscoverCatalogEntryV1 entry;
            Jularr.Integrations.Data.Tmdb.TmdbWork? tmdbWork = null;
            int? tvdbId = null;
            if (catalog)
            {
                entry = await _catalog.ReadEntryAsync(Context, Data.MediaTypeId, Data.MediaSubtypeId, source, Data.ExternalId, CancellationToken);
                if (anime && int.TryParse(Data.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var aniId))
                {
                    var mapped = await _bridge.ReadAsync(aniId, CancellationToken);
                    tvdbId = mapped?.TvdbSeriesId;
                }
            }
            else if (tmdb)
            {
                if (!int.TryParse(Data.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var externalId) || externalId <= 0)
                    throw new KeyNotFoundException("Invalid TMDB id.");
                var conn = await _metadata.ConnectAsync(Context, CancellationToken);
                tmdbWork = await _metadata.ReadWorkAsync(conn, Data.MediaTypeId, externalId, CancellationToken);
                tvdbId = tmdbWork.TvdbId;
                entry = new DiscoverCatalogEntryV1(tmdbWork.Title, tmdbWork.Year, tmdbWork.Overview,
                    TmdbClient.PosterUrl(tmdbWork.PosterPath), TmdbClient.BackdropUrl(tmdbWork.BackdropPath),
                    tmdbWork.Details.TrailerKey, null);
            }
            else
            {
                if (!int.TryParse(Data.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                    throw new KeyNotFoundException("Invalid TVDB id.");
                tvdbId = id;
                var session = await Context.ExecuteBatchAsync((ctx, ct) => _tvdb.ConnectAsync(ctx, ct), CancellationToken);
                var series = await _tvdb.ReadSeriesAsync(session, id, CancellationToken);
                entry = new DiscoverCatalogEntryV1(series.Name, null, null, null, null, null, null);
            }

            // A cached external item may have become an existing Work since it was listed.
            // Resolve only exact identities; never link by title.
            if (entry.WorkId is null)
            {
                var lookupNamespace = source == "tvdb" ? "tvdb" : source;
                var candidates = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    var sql = new SqlStatement();
                    sql.Statement = """
                        SELECT identity."WorkId"
                        FROM "WorkExternalIdentities" AS identity
                        JOIN "ProfileWorks" AS visible ON visible."WorkId" = identity."WorkId"
                            AND visible."ProfileId" = @ProfileId AND NOT visible."IsHidden"
                        WHERE identity."Namespace" = @Namespace AND identity."Value" = @ExternalId
                        LIMIT 2;
                        """;
                    sql.Parameters.Add(new { ProfileId = Auth.ProfileId!.Value, Namespace = lookupNamespace, Data.ExternalId });
                    return await ctx.ReadSql.ReadListAsync<ExternalDetailsWorkId>(sql, ct);
                }, CancellationToken, mode: SqlAccessMode.ReadOnly);
                if (candidates.Count == 1)
                    entry = new DiscoverCatalogEntryV1(entry.Title, entry.ReleaseYear, entry.Overview, entry.PosterUrl,
                        entry.BackdropUrl, entry.TrailerKey, candidates[0].WorkId) { Author = entry.Author, SeasonNumber = entry.SeasonNumber };
            }

            var seasons = new List<UserMedia.WorkSeasonV1>();
            var episodes = new List<UserMedia.WorkEpisodeV1>();
            if (Data.MediaTypeId == MediaType.Series && tvdbId is { } mappedId)
            {
                var structure = await _tvdb.TryReadStructureAsync(Context, mappedId, tmdbWork?.ImdbId, CancellationToken);
                if (structure is not null)
                {
                    var selected = entry.SeasonNumber ?? structure.Episodes.Where(item => item.SeasonNumber > 0)
                        .Select(item => (int?)item.SeasonNumber).Min() ?? structure.Episodes.Min(item => item.SeasonNumber);
                    foreach (var group in structure.Episodes.GroupBy(item => item.SeasonNumber).OrderBy(item => item.Key))
                    {
                        seasons.Add(new UserMedia.WorkSeasonV1(group.Key, null, null,
                            group.Where(item => item.Aired is not null).Select(item => (int?)item.Aired!.Value.Year).Min(),
                            group.Count(), 0, group.Key + 1L, 0));
                        if (group.Key != selected) continue;
                        foreach (var episode in group.OrderBy(item => item.EpisodeNumber))
                            episodes.Add(new UserMedia.WorkEpisodeV1(episode.Id, group.Key, episode.EpisodeNumber,
                                episode.Name, null, null, episode.Runtime, episode.Aired, null, null, null,
                                false, MediaStateType.NotRequested, episode.Aired is { } air && air > DateOnly.FromDateTime(DateTime.UtcNow), null));
                    }
                }
            }
            if (seasons.Count == 0 && tmdbWork is not null && Data.MediaTypeId == MediaType.Series)
                seasons.AddRange(tmdbWork.Seasons.OrderBy(item => item.SeasonNumber).Select(item => new UserMedia.WorkSeasonV1(item.SeasonNumber, item.Name,
                    TmdbClient.PosterUrl(item.PosterPath), item.AirDate?.Year, item.EpisodeCount, 0, item.SeasonNumber + 1L, 0)));

            int? progress = null, total = null;
            string? status = null;
            if (source == "anilist" && int.TryParse(Data.ExternalId, NumberStyles.None, CultureInfo.InvariantCulture, out var mediaId))
            {
                var synced = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    var sql = new SqlStatement();
                    sql.Statement = """
                        SELECT "Progress", "TotalCount", "Status"
                        FROM "AniListEntries"
                        WHERE "ProfileId" = @ProfileId AND "MediaId" = @MediaId;
                        """;
                    sql.Parameters.Add(new { ProfileId = Auth.ProfileId!.Value, MediaId = mediaId });
                    return await ctx.ReadSql.ReadOptionalAsync<ExternalAniListProgress>(sql, ct);
                }, CancellationToken, mode: SqlAccessMode.ReadOnly);
                progress = synced?.Progress;
                total = synced?.TotalCount;
                status = synced?.Status;
            }

            return new DiscoverExternalDetailsV1(entry, seasons, episodes, progress, total, status);
        }

        private sealed record ExternalDetailsWorkId(long WorkId);
        private sealed record ExternalAniListProgress(int Progress, int? TotalCount, string Status);
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
                return items is null ? null : new BaseDataList<DiscoverCatalogItemV1>([.. items.Select(DiscoverQueries.CatalogItem)]);
            }, CancellationToken, mode: SqlAccessMode.ReadOnly);
            if (cached is not null)
            {
                return cached;
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

        public static async Task RequireTypeAsync(LogicContext context, MediaType mediaType, long profileId, CancellationToken ct)
        {
            if (!(await ReadTypesAsync(context, profileId, ct)).Usable.Contains(mediaType))
            {
                throw new KeyNotFoundException("The media type is not available.");
            }
        }

        public static Task<IReadOnlyDictionary<int, DiscoverOverlayData>> ReadOverlayAsync(LogicContext context, MediaType mediaType, IEnumerable<int> tmdbIds, long profileId, CancellationToken ct) =>
            context.ExecuteBatchAsync((ctx, token) => ReadOverlayInBatchAsync(ctx, mediaType, tmdbIds, profileId, token), ct, mode: SqlAccessMode.ReadOnly);

        public static async Task<IReadOnlyDictionary<int, DiscoverOverlayData>> ReadOverlayInBatchAsync(LogicContext context, MediaType mediaType, IEnumerable<int> tmdbIds, long profileId,
            CancellationToken ct)
        {
            // Provider titles that already are Works carry their viewer state.
            var ids = JsonSerializer.Serialize(tmdbIds.Select(id => new { Namespace = WorkMetadata.Namespace(mediaType), Value = id.ToString(CultureInfo.InvariantCulture) }));
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
            var rows = await context.ReadSql.ReadListAsync<DiscoverOverlayData>(sql, ct);
            return rows.ToDictionary(row => int.Parse(row.Value, CultureInfo.InvariantCulture));
        }

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
