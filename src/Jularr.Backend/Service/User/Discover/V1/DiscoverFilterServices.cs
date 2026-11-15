using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Tmdb;

namespace Jularr.Backend.Service.User.Discover.V1
{
    // A page of a scope's titles as Discover's filters narrow them: from the whole catalog Jularr holds (AniList's, TMDB's films), else from the provider,
    // whose titles become Works like every card's.
    [ServiceEndpoint("POST", "/api/v1/discover/filtered")]
    public sealed class ReadDiscoverFilteredV1 : UserService<DiscoverParametersV1, ReadDiscoverFilteredDataV1>
    {
        private readonly WorkMetadata _metadata;
        private readonly MetadataCatalog _catalog;
        private readonly AniListMetadata _aniList;
        private readonly TimeProvider _time;

        internal ReadDiscoverFilteredV1(WorkMetadata metadata, MetadataCatalog catalog, AniListMetadata aniList, TimeProvider time)
        {
            _metadata = metadata;
            _catalog = catalog;
            _aniList = aniList;
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DiscoverFilteredV1>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(DiscoverParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Only what the scope's filter offers is asked for.
            var scope = DiscoverScopes.All.FirstOrDefault(scope => scope.Slug == Data.ScopeSlug);
            var options = scope is null ? null : DiscoverFilterScopes.Options(scope, new([]), new([]));
            if (scope is null || options is null)
            {
                throw new DataValidationException(nameof(ReadDiscoverFilteredDataV1.ScopeSlug), "unknown_catalog_value");
            }

            var sort = Data.DiscoverSortId ?? DiscoverSort.Popularity;
            if (!options.Sorts.Contains(sort) || Data.GenreIds.Any(genre => !options.GenreIds.Contains(genre)) || Data.CatalogFormatIds.Any(format => !options.CatalogFormatIds.Contains(format))
                || Data.CountryCode is { } country && !options.CountryCodes.Contains(country) || Data.LanguageCode is { } language && !options.LanguageCodes.Contains(language))
            {
                throw new DataValidationException(nameof(ReadDiscoverFilteredDataV1.ScopeSlug), "unknown_catalog_value");
            }

            var catalog = DiscoverScopes.Namespace(scope);
            var profileId = Auth.ProfileId!.Value;
            var local = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                if (!(await DiscoverQueries.ReadScopesAsync(ctx, profileId, ct)).Any(listed => listed.Slug == scope.Slug))
                {
                    throw new KeyNotFoundException("The media type is not available.");
                }
                if (!(await ctx.ReadWholeExternalWorkCatalogsAsync(ct)).Contains(catalog))
                {
                    return null;
                }

                var rows = await ctx.ReadExternalWorkShelvesAsync(new ExternalWorkShelfQueryData([DiscoverFilterScopes.Shelf(scope, Data, sort)], profileId,
                    DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime), (Data.Page - 1) * ExternalWorkLogic.ShelfSize), ct);
                return scope.IsCatalog
                    ? new DiscoverFilteredV1(new([]), new([.. rows.Select(DiscoverQueries.CatalogItem)]))
                    : new DiscoverFilteredV1(new([.. rows.Select(row => DiscoverQueries.ToItem(scope.MediaTypeId, row))]), new([]));
            }, CancellationToken, mode: SqlAccessMode.ReadOnly);
            if (local is not null)
            {
                return local;
            }

            // AniList narrows every filter; tags go by their names.
            if (scope.MediaSubtypeId is { } subtype && DiscoverScopes.IsAniList(subtype))
            {
                var tagNames = Data.TagIds.Length == 0 ? new Dictionary<int, string>() : (await _aniList.ReadFilterOptionsAsync(CancellationToken)).Tags.ToDictionary(tag => tag.Id, tag => tag.Name);
                var works = await _catalog.ReadFilteredAniListAsync(Context, subtype, DiscoverFilterScopes.AniListFormats(options, Data, subtype),
                    DiscoverFilterScopes.AniListQuery(Data, sort, tagNames), Data.Page, CancellationToken);
                return new DiscoverFilteredV1(new([]), await DiscoverQueries.CatalogItemsAsync(Context, works, profileId, CancellationToken));
            }

            if (scope.MediaTypeId == MediaType.Book)
            {
                var books = await _catalog.ReadFilteredBooksAsync(Context, DiscoverFilterScopes.BookSubjectsOf(Data.GenreIds), Data.YearFrom, Data.YearTo, Data.LanguageCode,
                    sort == DiscoverSort.Released, Data.Page, CancellationToken);
                return new DiscoverFilteredV1(new([]), await DiscoverQueries.CatalogItemsAsync(Context, books, profileId, CancellationToken));
            }

            if (scope.MediaTypeId == MediaType.Game)
            {
                var games = await _catalog.ReadFilteredGamesAsync(Context, DiscoverFilterScopes.IgdbQuery(Data, sort), Data.Page, CancellationToken);
                return new DiscoverFilteredV1(new([]), await DiscoverQueries.CatalogItemsAsync(Context, games, profileId, CancellationToken));
            }

            if (scope.MediaTypeId == MediaType.Music)
            {
                var albums = await _catalog.ReadFilteredMusicAsync(Context, DiscoverFilterScopes.MusicTagsOf(Data.GenreIds), Data.YearFrom, Data.YearTo, Data.Page, CancellationToken);
                return new DiscoverFilteredV1(new([]), await DiscoverQueries.CatalogItemsAsync(Context, albums, profileId, CancellationToken));
            }

            // TMDB's discover lists; a series is TheTVDB's, as on its lists.
            var connection = await _metadata.ConnectAsync(Context, CancellationToken);
            var results = await _metadata.DiscoverAsync(connection, scope.MediaTypeId, DiscoverFilterScopes.TmdbQuery(Data, sort, scope.MediaTypeId == MediaType.Series,
                TmdbClient.WatchRegion(connection)), Data.Page, CancellationToken);
            var kept = await MetadataCatalog.KeepWorksAsync(Context, new ExternalWorkListData(ExternalFeedLogic.CatalogOf(catalog), scope.MediaTypeId, null),
                await _metadata.ExternalWorksAsync(Context, connection, scope.MediaTypeId, results, CancellationToken), CancellationToken);
            return new DiscoverFilteredV1(await DiscoverQueries.ItemsAsync(Context, scope.MediaTypeId, kept, profileId, CancellationToken), new([]));
        }
    }

    // What a scope's filter offers; a provider that cannot name its tags or sites leaves them out.
    [ServiceEndpoint("POST", "/api/v1/discover/filter-options")]
    public sealed class ReadDiscoverFilterOptionsV1 : UserService<DiscoverParametersV1, ReadDiscoverFilterOptionsDataV1>
    {
        private static readonly TimeSpan NamesLimit = TimeSpan.FromSeconds(2.5);
        private readonly WorkMetadata _metadata;
        private readonly AniListMetadata _aniList;

        internal ReadDiscoverFilterOptionsV1(WorkMetadata metadata, AniListMetadata aniList)
        {
            _metadata = metadata;
            _aniList = aniList;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DiscoverFilterOptionsV1>()];
        protected override ModuleRequirement GetInstanceModules(DiscoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(DiscoverParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var scope = DiscoverScopes.All.FirstOrDefault(scope => scope.Slug == Data.ScopeSlug)
                ?? throw new DataValidationException(nameof(ReadDiscoverFilterOptionsDataV1.ScopeSlug), "unknown_catalog_value");
            if (DiscoverFilterScopes.Options(scope, new([]), new([])) is not { } offered)
            {
                return new DiscoverFilterOptionsV1([], [], [], [], [], [], [], [], new([]), new([]));
            }

            BaseDataList<DiscoverOptionV1> tags = new([]);
            BaseDataList<DiscoverOptionV1> sites = new([]);
            if (!Data.IncludesNames)
            {
                return offered;
            }

            // A provider that answers late never holds the page; its names come with a later one.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            limit.CancelAfter(NamesLimit);
            try
            {
                if (DiscoverScopes.IsAniList(scope.MediaSubtypeId))
                {
                    var aniList = await _aniList.ReadFilterOptionsAsync(limit.Token);
                    tags = new([.. aniList.Tags.OrderBy(tag => tag.Category, StringComparer.Ordinal).ThenBy(tag => tag.Name, StringComparer.Ordinal)
                        .Select(tag => new DiscoverOptionV1(tag.Id, tag.Name, tag.Category, tag.IsAdult))]);
                    sites = new([.. (scope.MediaSubtypeId == MediaSubtype.Anime ? aniList.AnimeSites : aniList.MangaSites).OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(site => new DiscoverOptionV1(site.Id, site.Name, null, false))]);
                }
                else if (offered.Filters.Contains(DiscoverFilter.Site) && scope.MediaTypeId is MediaType.Movie or MediaType.Series)
                {
                    var connection = await _metadata.ConnectAsync(Context, limit.Token);
                    sites = new([.. (await _metadata.ReadProvidersAsync(connection, scope.MediaTypeId, limit.Token)).Take(50)
                        .Select(provider => new DiscoverOptionV1(provider.Id, provider.Name, null, false))]);
                }
            }
            catch (Exception exception) when (exception is ServiceUnavailableException or KeyNotFoundException || exception is OperationCanceledException && !CancellationToken.IsCancellationRequested)
            {
                // The other filters stay useful without the provider's names.
            }

            return DiscoverFilterScopes.Options(scope, tags, sites)!;
        }
    }
}
