using System.Globalization;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Collection.V1;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.User.Favorites
{
    public sealed partial class FavoritesModel
    {
        private const string PickerId = "favorites-picker";

        private sealed record State(IReadOnlyList<string> Types, LibrarySortType Sort, string? Year, string? Status, long? Collection, int Page);

        private State Current => new(Picked, Sort, ValidYear, ValidStatus, CollectionId, Math.Max(1, PageNumber));

        private string? ValidYear => YearChoice.Valid(Year);

        private static readonly (string Slug, ProgressStateType State, string LabelKey)[] Statuses =
        [
            ("notstarted", ProgressStateType.NotStarted, "favorites.status.notStarted"), ("progress", ProgressStateType.InProgress, "favorites.status.inProgress"),
            ("finished", ProgressStateType.Finished, "favorites.status.finished")
        ];

        private string? ValidStatus => Statuses.Any(status => status.Slug == Status) ? Status : null;

        private ProgressStateType? ProgressState => Statuses.Where(status => status.Slug == ValidStatus).Select(status => (ProgressStateType?)status.State).FirstOrDefault();

        // The media types of the address against the types the profile lists.
        private ScopeSelection Scopes => _scopes ??= new ScopeSelection(Types, _media);

        private IReadOnlyList<string> Picked => Scopes.Picked;

        private bool IsAll => Scopes.IsAll;

        private string Address(State state)
        {
            var values = new List<string>();
            if (Scopes.Parameter(state.Types) is { } types) values.Add("type=" + Uri.EscapeDataString(types).Replace("%2C", ","));
            if (state.Sort != LibrarySortType.RecentlyAdded) values.Add("sort=" + state.Sort);
            if (state.Year is not null) values.Add("year=" + state.Year);
            if (state.Status is not null) values.Add("status=" + state.Status);
            if (state.Collection is { } collection) values.Add("collection=" + collection.ToString(CultureInfo.InvariantCulture));
            if (state.Page > 1) values.Add("p=" + state.Page.ToString(CultureInfo.InvariantCulture));
            return values.Count == 0 ? "/Favorites" : "/Favorites?" + string.Join('&', values);
        }

        // A handler of this page that keeps the choices of the address.
        private string Post(string handler, long? collectionId = null)
        {
            var address = Address(Current);
            return address + (address.Contains('?') ? "&" : "?") + "handler=" + handler + (collectionId is { } id ? "&collectionId=" + id.ToString(CultureInfo.InvariantCulture) : "");
        }

        protected override void Define(Page page)
        {
            page.Title("favorites.title");
            page.Navigation("nav.favorites");
            if (FormError is not null) page.Content.Add(new P(ErrorText.Of(FormError)).Class("ui-page-error").Attribute("role", "alert"));
            if (!_loaded) return;

            page.Asset("Pages/User/Favorites/Favorites.entry.scss");

            page.Asset("Framework/Components/MediaCard.entry.ts");
            page.Asset("Framework/Components/InfiniteList.entry.ts");
            page.Asset("Pages/User/Favorites/Favorites.entry.ts");
            page.Asset("Framework/Components/Drawer.entry.ts");
            page.Asset("Framework/Components/ChoiceSelect.entry.ts");
            page.Asset("Framework/Components/LayoutToggle.entry.ts");

            var canCollect = texts.CanCollect();
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var parts = new List<IUiComponent>();
            if (canCollect) parts.Add(CollectionStrip());
            parts.Add(TypeBar());
            parts.Add(Toolbar(canCollect));
            if (canCollect) parts.Add(BulkBar(csrf));

            // One page of titles, each with its collections beside the card.
            if (_entries.Count > 0) parts.Add(new Div([.. _entries.Select(entry => EntryCard(entry, csrf, canCollect))]).Class("media-grid favorites-grid"));
            else parts.Add(new P(new Text(EmptyKey())).Class("favorites-empty").Attribute("role", "status"));

            parts.Add(new Span().Infinite(_entries.Count, PageSize, "p", ".favorites-grid"));

            // Panels open over the page.
            parts.Add(FilterDrawer());
            if (canCollect)
            {
                parts.Add(CreateDrawer(csrf));
                parts.AddRange(_collections.Select(collection => EditDrawer(collection, csrf)));
                parts.Add(Picker(csrf));
            }

            page.Content.Add(new Div([.. parts]).Class("favorites"));
        }

        private string EmptyKey() => _current is { IsSmart: true } ? "collections.noMatch" : _current is not null ? "favorites.noWorks"
            : !IsAll || ValidYear is not null || ValidStatus is not null ? "favorites.noMatch" : "media.favorites.empty";

        // All favorites, then one card per collection with the covers of its first titles, and the way to a new one.
        private IUiComponent CollectionStrip()
        {
            var cards = new List<IUiComponent> { StripCard(null, "favorites.all", "heart", _newest.Select(item => item.PosterUrl), _total is { } total ? string.Format(CultureInfo.CurrentCulture, texts["favorites.titles"], total) : null, null) };
            foreach (var collection in _collections)
            {
                var count = collection.IsSmart ? texts["collections.smart"] : string.Format(CultureInfo.CurrentCulture, texts["favorites.titles"], collection.WorkCount ?? 0);
                cards.Add(StripCard(collection, null, collection.IsSmart ? "filter" : "folder", collection.PosterUrls, count, collection.Name));
            }

            cards.Add(Drawer.Opener(new Button(new Icon("plus", assets), new Span(new Text("collections.new"))).Class("favorites-collection__new"), "favorites-create"));
            return new Nav([.. cards]).Class("favorites-collections").Attribute("aria-label", texts["favorites.collections"]);
        }

        private IUiComponent StripCard(CollectionV1? collection, string? labelKey, string icon, IEnumerable<string?> posters, string? count, string? name)
        {
            var selected = collection?.Id == _current?.Id;
            var info = new List<IUiComponent> { new Icon(icon, assets, 16), new Strong(labelKey is null ? (IUiComponent)new ValueText(name ?? "") : new Text(labelKey)) };
            if (count is not null) info.Add(new Small(new ValueText(count)));
            var covers = posters.Where(url => !string.IsNullOrWhiteSpace(url)).Take(3).Select(url => (IUiComponent)new Span(new MediaImage(url!, "", 94, 144)).Class("favorites-collection__poster")).ToArray();
            var link = new A(new Span([.. info]).Class("favorites-collection__info"), new Span(covers).Class("favorites-collection__posters").Attribute("aria-hidden", "true"))
                .Class(selected ? "favorites-collection__card favorites-collection__card--current" : "favorites-collection__card")
                .Attribute("href", Address(Current with { Collection = collection?.Id, Page = 1 }));
            if (selected) link.Attribute("aria-current", "true");

            var wrapper = new List<IUiComponent> { link };
            if (collection is not null)
            {
                wrapper.Add(Drawer.Opener(new Button(new Icon("sliders", assets, 16)).IconOnly(string.Format(CultureInfo.CurrentCulture, texts["favorites.collection.edit"], collection.Name))
                    .Class("button button--icon favorites-collection__edit"), "favorites-edit-" + collection.Id.ToString(CultureInfo.InvariantCulture)));
            }

            return new Div([.. wrapper]).Class("favorites-collection");
        }

        // The shared media-type bar; the filter opens beside it.
        private IUiComponent TypeBar()
        {
            var filter = new List<IUiComponent> { new Icon("filter", assets) };
            if (ValidYear is not null) filter.Add(new Span(new ValueText("1")).Class("capsule-bar__badge"));
            var tools = new List<IUiComponent> { Drawer.Opener(new Button([.. filter]).IconOnly(texts["favorites.filter.open"]).Class("button button--icon capsule-bar__tool"), "favorites-filter") };
            return new MediaTypeBar(assets, Scopes.ListedSlugs, Scopes.ChosenSlugs, "library.scope.", types => Address(Current with { Types = types, Page = 1 }), tools);
        }

        // Progress, then selecting titles for their collections, the order and how the titles are laid out.
        private IUiComponent Toolbar(bool canCollect)
        {
            var keep = new Dictionary<string, string>();
            if (Scopes.Parameter(Picked) is { } types) keep["type"] = types;
            if (ValidYear is not null) keep["year"] = ValidYear;
            if (ValidStatus is not null) keep["status"] = ValidStatus;
            if (CollectionId is { } collection) keep["collection"] = collection.ToString(CultureInfo.InvariantCulture);
            var sorts = Enum.GetValues<LibrarySortType>().Select(sort => new ChoiceOption("library.sort." + sort, sort.ToString(), sort == Sort)).ToList();

            var tabs = new List<NavigationTab> { new("discover.all", Address(Current with { Status = null, Page = 1 }), ValidStatus is null) };
            tabs.AddRange(Statuses.Select(status => new NavigationTab(status.LabelKey, Address(Current with { Status = status.Slug, Page = 1 }), ValidStatus == status.Slug)));
            var settings = new List<IUiComponent>();
            if (canCollect)
            {
                settings.Add(new Button(new Icon("check", assets, 16), new Span(new Text("favorites.select")).Class("favorites-toolbar__label--off"), new Span(new Text("favorites.select.done")).Class("favorites-toolbar__label--on"))
                    .Class("button button--compact favorites-toolbar__select").Attribute("aria-pressed", "false").Attribute("data-favorites-bulk-toggle", ""));
            }

            settings.Add(new ChoiceSelect("/Favorites", "sort", "library.sort", sorts, keep));
            settings.Add(new LayoutToggle(assets));
            return new Div(new NavigationTabs(tabs, "favorites.status", "chips"), new Div([.. settings]).Class("favorites-toolbar__settings")).Class("favorites-toolbar");
        }

        // Actions on the selected titles; script shows it while selecting.
        private IUiComponent BulkBar(string? csrf)
        {
            var actions = new List<IUiComponent>
            {
                Opener(new Button(new Icon("folder", assets, 16), new Text("favorites.assign")).Class("button button--compact").Attribute("data-favorites-picker", "bulk"))
            };
            if (_current is { IsSmart: false })
            {
                actions.Add(new PostForm(Post("RemoveFromCollection"), csrf, [], new Button(new Text("favorites.removeFromCollection")).Submit().Class("button button--compact"))
                    .Attribute("data-favorites-bulk-form", ""));
            }

            actions.Add(new Button(new Text("favorites.clearSelection")).Class("button button--compact").Attribute("data-favorites-clear", ""));
            var count = new Strong(new ValueText("")).Attribute("data-favorites-count", "").Attribute("data-label", texts["favorites.selected"]);
            return new Div(new Div(count, new Button(new Text("favorites.selectShown")).Class("button button--compact").Attribute("data-favorites-select-shown", "")).Class("favorites-bulk__status"),
                new Div([.. actions]).Class("favorites-bulk__actions")).Class("favorites-bulk").Attribute("hidden", "hidden").Attribute("data-favorites-bulk", "").Attribute("aria-live", "polite");
        }

        private static HtmlElement Opener(HtmlElement button) => button.Attribute("popovertarget", PickerId);

        // A card with the collections it is in; the buttons around it need script.
        private IUiComponent EntryCard(FavoriteEntry entry, string? csrf, bool canCollect)
        {
            var id = entry.Id.ToString(CultureInfo.InvariantCulture);
            // With only favorites the heart toggles; with collections it opens the popover (the toggle stays without script).
            IUiComponent heart = new MediaBookmark(assets, entry.Id, entry.IsFavorite, Request.Path + Request.QueryString, csrf);
            if (canCollect && _collections.Any(collection => !collection.IsSmart))
            {
                heart = new Div(new Div(heart).Class("favorites-heart__toggle"),
                    Opener(new Button(new Icon("heart", assets)).IconOnly(string.Format(CultureInfo.CurrentCulture, texts["favorites.heart.item"], entry.Title))
                        .Class("button button--icon favorites-heart__manage").Attribute("aria-pressed", entry.IsFavorite ? "true" : "false").Attribute("data-favorites-picker", id)));
            }

            var card = MediaCard.For(assets, entry.Item).Bookmark(heart);
            if (entry.Progress != ProgressStateType.NotStarted)
            {
                card.Label(new Span(entry.Progress == ProgressStateType.Finished ? new Text("favorites.status.finished") : new ValueText(entry.ProgressPercent.ToString(CultureInfo.CurrentCulture) + "%"))
                    .Class("favorites-entry__badge"));
            }

            var parts = new List<IUiComponent> { card };

            var holding = _membership.TryGetValue(entry.Id, out var held) ? held : [];
            if (canCollect)
            {
                var names = holding.Select(collectionId => _collections.FirstOrDefault(collection => collection.Id == collectionId)?.Name).Where(name => name is not null).ToList();
                IUiComponent label = names.Count == 0 ? new Text("favorites.chip.none")
                    : new ValueText(names[0] + (names.Count > 1 ? " +" + (names.Count - 1).ToString(CultureInfo.CurrentCulture) : ""));
                parts.Add(Opener(new Button(new Icon(names.Count == 0 ? "plus" : "folder", assets, 14), new Span(label)).Class(names.Count == 0 ? "favorites-chip" : "favorites-chip favorites-chip--set")
                    .Attribute("data-favorites-picker", id)));
                parts.Add(new Button(new Icon("check", assets, 18)).IconOnly(string.Format(CultureInfo.CurrentCulture, texts["favorites.select.item"], entry.Title))
                    .Class("favorites-entry__select").Attribute("aria-pressed", "false").Attribute("data-favorites-select", id));
            }

            return new Div([.. parts]).Class("favorites-entry").Attribute("data-list-key", id).Attribute("data-favorites-entry", id).Attribute("data-title", entry.Title)
                .Attribute("data-collections", string.Join(',', holding.Select(collectionId => collectionId.ToString(CultureInfo.InvariantCulture))));
        }

        // One popover for every title: script fills in which titles it is for and which collections hold them.
        private IUiComponent Picker(string? csrf)
        {
            var options = _collections.Where(collection => !collection.IsSmart).Select(collection => (IUiComponent)new Label(
                new Input().Attribute("type", "checkbox").Attribute("name", "collectionIds").Attribute("value", collection.Id.ToString(CultureInfo.InvariantCulture)),
                new Icon("folder", assets, 16),
                new Span(new Strong(new ValueText(collection.Name)), new Small(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["favorites.titles"], collection.WorkCount ?? 0)))).Class("favorites-picker__text"))
                .Class("favorites-picker__option")).ToList();
            var create = new Div(new Input().Attribute("type", "text").Attribute("name", "newName").Attribute("maxlength", "200").Attribute("autocomplete", "off")
                .Attribute("aria-label", texts["favorites.picker.newName"]).TextAttribute("placeholder", "favorites.picker.newName"),
                new Button(new Icon("plus", assets, 16)).IconOnly(texts["favorites.picker.create"]).Submit()).Class("favorites-picker__create");
            var footer = new Div(new Button(new Icon("heart", assets, 16), new Text("media.favorites.remove")).Submit().Class("button button--compact favorites-picker__remove")
                    .Attribute("formaction", Post("Remove")).Attribute("data-favorites-remove", ""),
                new Button(new Text("common.done")).Primary().Submit()).Class("favorites-picker__footer");
            var form = new PostForm(Post("Lists"), csrf, [],
                new Div(new Strong(new Text("favorites.collections")), new Span().Attribute("data-favorites-subject", "")).Class("favorites-picker__heading"),
                new Div([.. options.Count > 0 ? options : [new P(new Text("collections.empty")).Class("favorites-picker__none")]]).Class("favorites-picker__options"),
                create, footer, new Span().Attribute("data-favorites-works", "").Attribute("hidden", "hidden"));
            return new Div(form).Class("favorites-picker").Attribute("id", PickerId).Attribute("popover", "auto");
        }

        // Types by area and the release year; the sort and the collection stay in the address.
        private IUiComponent FilterDrawer()
        {
            var keep = new Dictionary<string, string>();
            if (Sort != LibrarySortType.RecentlyAdded) keep["sort"] = Sort.ToString();
            if (CollectionId is { } collection) keep["collection"] = collection.ToString(CultureInfo.InvariantCulture);
            return new MediaFilterDrawer(assets, "favorites-filter", "favorites.filter.title", "/Favorites", Scopes, ValidYear, Address(Current with { Types = [], Year = null, Page = 1 }), keep);
        }

        private IUiComponent CreateDrawer(string? csrf) => OnePane("favorites-create", "collections.new", "plus",
        [
            new Form<CreateCollectionDataV1>(form =>
            {
                form.Text(item => item.Name, "collections.name");
                form.Toggle(item => item.IsSmart, "collections.smartToggle");
                form.Submit("collections.create");
            }, Post("Create"), csrf, new CreateCollectionDataV1("", false))
        ], [new Button(new Text("common.close")).Attribute("data-drawer-close", "")]);

        // Rename and delete; a filter is edited on its own page.
        private IUiComponent EditDrawer(CollectionV1 collection, string? csrf)
        {
            var body = new List<IUiComponent>
            {
                new Form<UpdateCollectionDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Text(item => item.Name, "collections.name");
                    form.Submit("common.save");
                }, Post("Rename", collection.Id), csrf, new UpdateCollectionDataV1(collection.Name, collection.Version))
            };
            if (collection.IsSmart) body.Add(new A(new Text("favorites.collection.filter")).Class("button").Attribute("href", "/Collections/" + collection.Id.ToString(CultureInfo.InvariantCulture)));
            body.Add(new Div(new P(new Text("collections.deleteConfirm")),
                new Form<CollectionParametersV1>(form => form.Submit("collections.delete"), Post("DeleteCollection", collection.Id), csrf, new CollectionParametersV1(collection.Id))).Class("favorites-edit__delete"));
            return OnePane("favorites-edit-" + collection.Id.ToString(CultureInfo.InvariantCulture), "collections.edit", "sliders", body, [new Button(new Text("common.close")).Attribute("data-drawer-close", "")]);
        }

        private Drawer OnePane(string id, string titleKey, string icon, IReadOnlyList<IUiComponent> body, IReadOnlyList<IUiComponent> footer, string? formAddress = null) =>
            new(assets, id, [new DrawerPane("main", titleKey, titleKey, icon, body, footer, formAddress)]);

        private sealed class Strong(params IUiComponent[] children) : HtmlElement("strong", children);

        private sealed class Small(params IUiComponent[] children) : HtmlElement("small", children);

        private sealed class Label(params IUiComponent[] children) : HtmlElement("label", children);
    }
}
