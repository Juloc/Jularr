using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Collection.V1;
using Jularr.Backend.Data.User.Library.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Data.User.Profile.V1;
using Jularr.Backend.Service.Public.Authentication.V1;
using Jularr.Backend.Service.User.Collection.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Backend.Service.User.Profile.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.Favorites
{
    // The profile's favorites by media type, in the chosen order, and its collections of them; each title with its state and the collections it is in.
    public sealed partial class FavoritesModel(ReadAuthenticationV1 authentication, ReadWatchlistV1 read, ReadProfileMediaV1 readMedia, UpdateWatchlistV1 update, ReadWatchlistCountV1 readCount, ReadCollectionsV1 readCollections,
        ReadCollectionV1 readCollection, ReadCollectionWorksV1 readCollectionWorks, ReadCollectionMembershipV1 readMembership, CreateCollectionV1 createCollection,
        UpdateCollectionV1 updateCollection, DeleteCollectionV1 deleteCollection, AddCollectionWorkV1 addWork, RemoveCollectionWorkV1 removeWork, IAntiforgery antiforgery, UiText texts,
        FrontendAssets assets) : JularrPage
    {
        private const int PageSize = 48;

        private IReadOnlyList<ProfileMediaV1> _media = [];
        private IReadOnlyList<CollectionV1> _collections = [];
        private IReadOnlyList<FavoriteEntry> _entries = [];
        private IReadOnlyList<LibraryItemV1> _newest = [];
        private IReadOnlyDictionary<long, long[]> _membership = new Dictionary<long, long[]>();
        private CollectionV1? _current;
        private bool _loaded;
        private bool _hasMore;
        private int? _total;

        // Media types as slugs, repeated or separated by commas; none shows all of them.
        [BindProperty(SupportsGet = true, Name = "type")]
        public string[] Types { get; set; } = [];

        [BindProperty(SupportsGet = true, Name = "sort")]
        public LibrarySortType Sort { get; set; } = LibrarySortType.RecentlyAdded;

        // Not started, in progress or finished; none shows every title.
        [BindProperty(SupportsGet = true, Name = "status")]
        public string? Status { get; set; }

        [BindProperty(SupportsGet = true, Name = "year")]
        public string? Year { get; set; }

        // The collection whose titles are shown instead of all favorites.
        [BindProperty(SupportsGet = true, Name = "collection")]
        public long? CollectionId { get; set; }

        [BindProperty(SupportsGet = true, Name = "p")]
        public int PageNumber { get; set; } = 1;

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            var token = HttpContext.RequestAborted;
            var profileId = (await authentication.ExecuteAsync<AuthenticationResultV1>(cancellationToken: token)).ProfileId ?? throw new UnauthorizedAccessException("An active profile is required.");
            _media = await readMedia.ExecuteAsync<BaseDataList<ProfileMediaV1>>(new ReadProfileParametersV1(profileId), token);

            // Collections shape the strip; the newest favorites give the first card its covers.
            if (texts.CanCollect())
            {
                _collections = await readCollections.ExecuteAsync<BaseDataList<CollectionV1>>(new CollectionsParametersV1(), token);
                _current = _collections.FirstOrDefault(collection => collection.Id == CollectionId);
                _newest = (await read.ExecuteAsync<PageResult<LibraryItemV1>>(new ReadWatchlistParametersV1(1, 4), token)).Items;
                _total = (await readCount.ExecuteAsync<WatchlistCountV1>(new ReadWatchlistCountParametersV1(), token)).Total;
            }

            // One bounded page of the favorites or of the chosen collection.
            var (types, subtypes) = ScopeFilter();
            var page = Math.Max(1, PageNumber);
            var (yearFrom, yearTo) = YearRange();
            if (_current is { } collection)
            {
                var works = await readCollectionWorks.ExecuteAsync<PageResult<LibraryItemV1>>(new CollectionWorksParametersV1(collection.Id, page, PageSize, types, subtypes, yearFrom, yearTo, ProgressState, Sort), token);
                _entries = [.. works.Items.Select(Entry)];
                _hasMore = works.Items.Count == works.PageSize;
            }
            else
            {
                var works = await read.ExecuteAsync<PageResult<LibraryItemV1>>(new ReadWatchlistParametersV1(page, PageSize, types, subtypes, yearFrom, yearTo, ProgressState, Sort), token);
                _entries = [.. works.Items.Select(Entry)];
                _hasMore = works.Items.Count == works.PageSize;
            }

            // Which hand-picked collections hold the shown titles, in one read.
            if (_entries.Count > 0 && _collections.Any(collection => !collection.IsSmart))
            {
                var members = await readMembership.ExecuteAsync<BaseDataList<CollectionMemberV1>>(new CollectionMembershipParametersV1([.. _entries.Select(entry => entry.Id)]), token);
                _membership = members.GroupBy(member => member.WorkId).ToDictionary(group => group.Key, group => group.Select(member => member.CollectionId).ToArray());
            }

            _loaded = true;
        }

        private static FavoriteEntry Entry(LibraryItemV1 item) => new(item);

        // A favorite that goes leaves its collections too, which the notice tells.
        public async Task<IActionResult> OnPostRemoveAsync(long[]? workIds)
        {
            var token = HttpContext.RequestAborted;
            var titles = (workIds ?? []).Distinct().ToArray();
            if (titles.Length > 0 && texts.CanCollect() && (await readMembership.ExecuteAsync<BaseDataList<CollectionMemberV1>>(new CollectionMembershipParametersV1(titles), token)).Count > 0)
            {
                Hint("favorites.removedFromCollections");
            }

            foreach (var workId in titles)
            {
                await update.ExecuteAsync<UpdateResult>(new WorkParametersV1(workId), new UpdateWatchlistDataV1(false), HttpContext.RequestAborted);
            }

            return LocalRedirect(Address(Current));
        }

        // Titles are put into the checked collections and taken out of the unchecked ones that held all of them.
        public async Task<IActionResult> OnPostListsAsync(long[]? workIds, long[]? collectionIds, string? newName)
        {
            var token = HttpContext.RequestAborted;
            var titles = (workIds ?? []).Distinct().ToArray();
            var chosen = (collectionIds ?? []).ToHashSet();
            if (titles.Length == 0) return LocalRedirect(Address(Current));

            var collections = (await readCollections.ExecuteAsync<BaseDataList<CollectionV1>>(new CollectionsParametersV1(), token)).Where(collection => !collection.IsSmart).ToList();
            var members = await readMembership.ExecuteAsync<BaseDataList<CollectionMemberV1>>(new CollectionMembershipParametersV1(titles), token);
            var held = members.ToLookup(member => member.CollectionId, member => member.WorkId);

            // A new collection takes every title.
            if (!string.IsNullOrWhiteSpace(newName))
            {
                var created = await createCollection.ExecuteAsync<CreateCollectionResultV1>(new CollectionsParametersV1(), new CreateCollectionDataV1(newName, false), token);
                foreach (var workId in titles)
                {
                    await addWork.ExecuteAsync<UpdateResult>(new CollectionWorkParametersV1(created.Id, workId), default, token);
                }
            }

            foreach (var collection in collections)
            {
                var holding = held[collection.Id].ToHashSet();
                if (chosen.Contains(collection.Id))
                {
                    foreach (var workId in titles.Where(workId => !holding.Contains(workId)))
                    {
                        await addWork.ExecuteAsync<UpdateResult>(new CollectionWorkParametersV1(collection.Id, workId), default, token);
                    }
                }
                else if (holding.Count == titles.Length)
                {
                    foreach (var workId in titles)
                    {
                        await removeWork.ExecuteAsync<UpdateResult>(new CollectionWorkParametersV1(collection.Id, workId), default, token);
                    }
                }
            }

            return LocalRedirect(Address(Current));
        }

        // The chosen collection lets go of the selected titles; they stay in favorites.
        public async Task<IActionResult> OnPostRemoveFromCollectionAsync(long[]? workIds)
        {
            if (CollectionId is { } collectionId)
            {
                foreach (var workId in workIds ?? [])
                {
                    await removeWork.ExecuteAsync<UpdateResult>(new CollectionWorkParametersV1(collectionId, workId), default, HttpContext.RequestAborted);
                }
            }

            return LocalRedirect(Address(Current));
        }

        public async Task<IActionResult> OnPostCreateAsync(string? name, bool isSmart)
        {
            var created = await createCollection.ExecuteAsync<CreateCollectionResultV1>(new CollectionsParametersV1(), new CreateCollectionDataV1(name ?? "", isSmart), HttpContext.RequestAborted);

            // A filter is set up on its own page.
            return LocalRedirect(isSmart ? "/Collections/" + created.Id.ToString(CultureInfo.InvariantCulture) : Address(Current with { Collection = created.Id, Page = 1 }));
        }

        public async Task<IActionResult> OnPostRenameAsync([BindRequired] long collectionId, string? name, [BindRequired] long version)
        {
            var token = HttpContext.RequestAborted;
            var collection = await readCollection.ExecuteAsync<CollectionV1>(new CollectionParametersV1(collectionId), token);
            await updateCollection.ExecuteAsync<UpdateResult>(new CollectionParametersV1(collectionId), new UpdateCollectionDataV1(name ?? "", version, collection.MediaTypeId, collection.MediaSubtypeId,
                collection.ReleaseYearFrom, collection.ReleaseYearTo, collection.OnlyAvailable, collection.OnlyUnfinished), token);
            return LocalRedirect(Address(Current));
        }

        public async Task<IActionResult> OnPostDeleteCollectionAsync([BindRequired] long collectionId)
        {
            await deleteCollection.ExecuteAsync<UpdateResult>(new CollectionParametersV1(collectionId), default, HttpContext.RequestAborted);
            return LocalRedirect(Address(Current with { Collection = CollectionId == collectionId ? null : CollectionId, Page = CollectionId == collectionId ? 1 : Current.Page }));
        }
    }

    // One title of the list with what its card shows.
    public sealed record FavoriteEntry(LibraryItemV1 Item)
    {
        public long Id => Item.Id;

        public string Title => Item.Title;

        public bool IsFavorite => Item.IsWatchlisted;

        public ProgressStateType Progress => Item.ProgressStateId;

        public int ProgressPercent => Item.ProgressPercent;
    }
}
