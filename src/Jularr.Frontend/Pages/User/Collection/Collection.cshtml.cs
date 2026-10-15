using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Collection.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.User.Collection.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.Collection
{
    // One collection: its Works, its name and, for a smart one, its filter.
    public sealed class CollectionModel(ReadCollectionV1 read, ReadCollectionWorksV1 readWorks, UpdateCollectionV1 update, DeleteCollectionV1 delete, RemoveCollectionWorkV1 removeWork,
        IAntiforgery antiforgery,
        UiText texts) : JularrPage
    {
        private CollectionV1? _collection;
        private PageResult<WorkCardV1>? _works;
        private UpdateCollectionDataV1? _input;

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        // Deleting asks once more.
        [BindProperty(SupportsGet = true)]
        public bool Delete { get; set; }

        private string Home => "/Collections/" + Id.ToString(CultureInfo.InvariantCulture);

        protected override void Define(Page page)
        {
            page.Title("collections.title", _collection?.Name);
            page.Navigation("collections.title");
            page.Asset("Pages/User/Collection/Collection.entry.scss");
            if (_collection is not { } collection)
            {
                if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
                return;
            }

            page.Content.Add(new H2(new ValueText(collection.Name)));
            if (_works is { } works)
            {
                // A hand-picked collection removes its titles one by one; a smart one follows its filter.
                var removeToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                page.Content.Add(new Div([.. works.Items.Select(item =>
                {
                    var card = new MediaCard(item.CanonicalTitle, "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture), item.PosterUrl,
                        item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId));
                    return collection.IsSmart ? (IUiComponent)card : new Div(card, new Form<CollectionWorkParametersV1>(form => form.Submit("media.remove", item.CanonicalTitle, valueHidden: true),
                        Home + "?handler=Remove&workId=" + item.Id.ToString(CultureInfo.InvariantCulture), removeToken, new CollectionWorkParametersV1(collection.Id, item.Id))).Class("media-grid__entry");
                })]).Class("media-grid"));
                if (works.Items.Count == 0) page.Content.Add(new P(new Text(collection.IsSmart ? "collections.noMatch" : "collections.noWorks")));
                if (works.Page > 1) page.Content.Add(new A(new Text("common.previous")).Class("button").Attribute("href", Home + "?pageNumber=" + (works.Page - 1).ToString(CultureInfo.InvariantCulture)));
                if (works.Items.Count == works.PageSize) page.Content.Add(new A(new Text("common.next")).Class("button").Attribute("href", Home + "?pageNumber=" + (works.Page + 1).ToString(CultureInfo.InvariantCulture)));
            }

            // Name, and for a smart collection the filter.
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var types = new[] { new SelectOption("", texts["collections.anyType"]) }.Concat(Enum.GetValues<MediaType>().Select(type => new SelectOption(type.ToString(), texts[MediaTypeLabel.Key(type, null)])));
            var subtypes = new[] { new SelectOption("", texts["collections.anySubtype"]) }.Concat(Enum.GetValues<MediaSubtype>().Select(subtype => new SelectOption(subtype.ToString(), texts["media.subtype." + subtype])));
            page.Content.Add(new UiSection("collections.edit", [new Form<UpdateCollectionDataV1>(form =>
            {
                form.Hidden(item => item.Version);
                form.Text(item => item.Name, "collections.name");
                if (collection.IsSmart)
                {
                    form.Select(item => item.MediaTypeId, "collections.type", types);
                    form.Select(item => item.MediaSubtypeId, "collections.subtype", subtypes);
                    form.Number(item => item.ReleaseYearFrom, "collections.yearFrom");
                    form.Number(item => item.ReleaseYearTo, "collections.yearTo");
                    form.Toggle(item => item.OnlyAvailable, "collections.onlyAvailable");
                    form.Toggle(item => item.OnlyUnfinished, "collections.onlyUnfinished");
                }

                form.Submit("common.save");
            }, Home, csrf, _input ?? new UpdateCollectionDataV1(collection.Name, collection.Version, collection.MediaTypeId, collection.MediaSubtypeId, collection.ReleaseYearFrom,
                collection.ReleaseYearTo, collection.OnlyAvailable, collection.OnlyUnfinished), FormError),
                Delete
                    ? new Form<CollectionParametersV1>(form => form.Submit("collections.delete"), Home + "?handler=Delete", csrf, new CollectionParametersV1(collection.Id))
                    : new A(new Text("collections.delete")).Class("button").Attribute("href", Home + "?delete=true")]));
            if (Delete) page.Content.Add(new P(new Text("collections.deleteConfirm")).Attribute("role", "alert"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            var token = HttpContext.RequestAborted;
            _collection = await read.ExecuteAsync<CollectionV1>(new CollectionParametersV1(Id), token);
            _works = await readWorks.ExecuteAsync<PageResult<WorkCardV1>>(new CollectionWorksParametersV1(Id, Math.Max(1, PageNumber)), token);
        }

        public async Task<IActionResult> OnPostAsync(string? name, [BindRequired] long version, MediaType? mediaTypeId, MediaSubtype? mediaSubtypeId, int? releaseYearFrom, int? releaseYearTo,
            bool onlyAvailable, bool onlyUnfinished)
        {
            _input = new UpdateCollectionDataV1(name ?? "", version, mediaTypeId, mediaSubtypeId, releaseYearFrom, releaseYearTo, onlyAvailable, onlyUnfinished);
            await update.ExecuteAsync<UpdateResult>(new CollectionParametersV1(Id), _input, HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostRemoveAsync([BindRequired] long workId)
        {
            await removeWork.ExecuteAsync<UpdateResult>(new CollectionWorkParametersV1(Id, workId), default, HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostDeleteAsync()
        {
            await delete.ExecuteAsync<UpdateResult>(new CollectionParametersV1(Id), default, HttpContext.RequestAborted);
            return LocalRedirect("/Collections");
        }
    }
}
