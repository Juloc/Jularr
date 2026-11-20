using System.Globalization;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Work;

namespace Jularr.Frontend.Pages.Admin.Search
{
    public sealed partial class SearchModel
    {
        // The tokens a type inserts into the query (Prowlarr's search options); a game's are Jularr's own.
        private static readonly (IndexerSearchType Type, string Token)[] Tokens =
        [
            (IndexerSearchType.Tv, "{ImdbId:tt1234567}"), (IndexerSearchType.Tv, "{TvdbId:12345}"), (IndexerSearchType.Tv, "{TmdbId:12345}"), (IndexerSearchType.Tv, "{TvMazeId:12345}"),
            (IndexerSearchType.Tv, "{Season:00}"), (IndexerSearchType.Tv, "{Episode:00}"),
            (IndexerSearchType.Movie, "{ImdbId:tt1234567}"), (IndexerSearchType.Movie, "{TmdbId:12345}"), (IndexerSearchType.Movie, "{Year:2000}"),
            (IndexerSearchType.Music, "{Artist:}"), (IndexerSearchType.Music, "{Album:}"), (IndexerSearchType.Music, "{Label:}"),
            (IndexerSearchType.Book, "{Author:}"), (IndexerSearchType.Book, "{Title:}"),
            (IndexerSearchType.Game, "{Platform:PS5}"), (IndexerSearchType.Game, "{Platform:PC}"), (IndexerSearchType.Game, "{Year:2026}")
        ];

        // The columns in Prowlarr's order; Dateien is hidden until a person shows it. Protocol, age and title always show.
        private static readonly string[] Columns = ["protocol", "age", "title", "indexer", "size", "files", "grabs", "peers", "category", "flags"];

        // The bar: the query with its type, the indexers and the categories, and the button. Enter searches.
        private IUiComponent Bar()
        {
            var query = new Input().Attribute("type", "search").Attribute("name", "q").Attribute("value", Query ?? "").Attribute("autocomplete", "off").Attribute("enterkeyhint", "search")
                .Class("search-bar__input").TextAttribute("placeholder", "search.query.placeholder").TextAttribute("aria-label", "search.query");
            var types = Enum.GetValues<IndexerSearchType>().Select(type => (IUiComponent)new Checkbox(new Text("search.type." + type), "type", type.ToString(), type == Type, radio: true));
            var tokens = Tokens.Select(token => (IUiComponent)new Button(new ValueText(token.Token)).Compact().Soft().Attribute("type", "button")
                .Attribute("data-token", token.Token).Attribute("data-for", token.Type.ToString())
                .Class("button button--compact button--soft search-token" + (token.Type == Type ? "" : " is-hidden")));
            var typeMenu = new ActionMenu(new Span(new Text("search.type." + Type)).Attribute("data-type-label", ""),
                [new Div([.. types]).Class("search-menu__group").Attribute("role", "radiogroup").TextAttribute("aria-label", "search.type"),
                 new P(new Text("search.tokens")).Class("search-menu__hint"), new Div([.. tokens]).Class("search-menu__tokens")],
                "button button--compact button--soft search-bar__type", "search.type");
            var chosen = Indexers.ToHashSet();
            var indexers = _sources.Select(source => (IUiComponent)new Label(
                Box("indexer", Number(source.Id), chosen.Contains(source.Id)), new Span(new ValueText(source.Name)), Protocol(source.ReleaseProtocolId)).Class("search-menu__option"));
            return new FormElement(
                new Div(query, typeMenu).Class("search-bar__query"),
                Picker("search.indexers", chosen.Count == 0 ? texts["search.indexers.all"] : Counted("search.indexers.some", chosen.Count), [.. indexers], "indexers"),
                Picker("search.categories", Categories.Length == 0 ? texts["search.categories.all"] : Counted("search.categories.some", Categories.Length), CategoryTree(), "categories"),
                LanguageFilter(),
                new NoScript(new Input().Attribute("type", "hidden").Attribute("name", "inline").Attribute("value", "true")),
                new Button(new Icon("search", assets, 16), new Span(new Text("search.start"))).Primary().Submit())
                .Class("search-bar").Attribute("method", "get").Attribute("action", "/Admin/Search").Attribute("role", "search").Attribute("hx-boost", "false")
                .TextAttribute("data-indexers-all", "search.indexers.all").TextAttribute("data-indexers-some", "search.indexers.some").TextAttribute("data-indexers-one", "search.indexers.some.one")
                .TextAttribute("data-categories-all", "search.categories.all").TextAttribute("data-categories-some", "search.categories.some")
                .TextAttribute("data-categories-one", "search.categories.some.one").TextAttribute("data-language-all", "search.language.all")
                .TextAttribute("data-language-from", "search.language.from").TextAttribute("data-language-only", "search.language.only");
        }

        private static IUiComponent Box(string name, string value, bool isChecked)
        {
            var box = new Input().Attribute("type", "checkbox").Attribute("name", name).Attribute("value", value);
            return isChecked ? box.Attribute("checked", "checked") : box;
        }

        // A field of the bar that opens a list of checkboxes: its label and what is chosen.
        private static IUiComponent Picker(string labelKey, string value, IReadOnlyList<IUiComponent> options, string name) =>
            new ActionMenu(new Span(new Span(new Text(labelKey)).Class("search-bar__label"), new Span(new ValueText(value)).Class("search-bar__value").Attribute("data-picker-value", name)),
                [new Div([.. options]).Class("search-menu__list")], "search-bar__picker", labelKey);

        // The Newznab categories the indexers offer, the main ones with theirs below; each names the indexers that offer it, so the list follows the chosen indexers.
        private IUiComponent[] CategoryTree()
        {
            var offered = _sources.SelectMany(source => source.Categories.Select(category => (Category: category, source.Id))).Where(item => NewznabCategories.Known(item.Category))
                .GroupBy(item => item.Category).ToDictionary(group => group.Key, group => group.Select(item => item.Id).ToHashSet());
            foreach (var parent in offered.Keys.Select(category => category / 1000 * 1000).Distinct().ToArray())
            {
                if (!offered.ContainsKey(parent)) offered[parent] = [.. offered.Where(pair => pair.Key / 1000 == parent / 1000).SelectMany(pair => pair.Value)];
            }

            var chosen = Categories.ToHashSet();
            return [.. offered.OrderBy(pair => pair.Key).Select(pair => (IUiComponent)new Label(Box("cat", Number(pair.Key), chosen.Contains(pair.Key)),
                    new Span(new Text(NewznabCategories.Key(pair.Key))), new Span(new ValueText(Number(pair.Key))).Class("search-menu__id"))
                .Class(pair.Key % 1000 == 0 ? "search-menu__option" : "search-menu__option search-menu__option--child")
                .Attribute("data-offered-by", string.Join(' ', pair.Value.Order().Select(id => Number(id)))))];
        }

        // Filter, order and columns of the loaded releases: nothing is asked again. Only with script.
        private IUiComponent Toolbar()
        {
            static IUiComponent Choice(string value, string key) => new Option(new Text(key)).Attribute("value", value);
            var filter = new Select(Choice("", "search.filter.all"), Choice("usenet", "search.filter.usenet"), Choice("torrent", "search.filter.torrent"), Choice("custom", "search.filter.custom"))
                .Class("search-tools__select").Attribute("data-filter-preset", "").TextAttribute("aria-label", "search.filter");
            IUiComponent Field(string name, string key, string type = "number") => new Label(new Span(new Text(key)),
                new Input().Attribute("type", type).Attribute("data-filter-field", name).Attribute("min", "0").Attribute("inputmode", type == "number" ? "decimal" : "text")).Class("search-filter__field");
            var custom = new ActionMenu(new Span(new Icon("filter", assets, 16), new Span(new Text("search.filter.edit")).Class("search-tools__text")),
                [Field("title", "search.column.title", "search"), Field("indexer", "search.column.indexer", "search"), Field("maxAge", "search.filter.maxAge"),
                 Field("minSize", "search.filter.minSize"), Field("maxSize", "search.filter.maxSize"), Field("minFiles", "search.filter.minFiles"), Field("minGrabs", "search.filter.minGrabs"),
                 Field("minSeeders", "search.filter.minSeeders"), Field("minPeers", "search.filter.minPeers")],
                "button button--compact button--soft search-tools__custom", "search.filter.edit");
            var sort = new Select(Choice("age", "search.column.age"), Choice("title", "search.column.title"), Choice("indexer", "search.column.indexer"), Choice("size", "search.column.size"),
                Choice("files", "search.column.files"), Choice("grabs", "search.column.grabs"), Choice("peers", "search.column.peers"), Choice("protocol", "search.column.protocol"),
                Choice("category", "search.column.category")).Class("search-tools__select").Attribute("data-sort-select", "").TextAttribute("aria-label", "search.sort");
            var columns = new ActionMenu(new Span(new Icon("table", assets, 16), new Span(new Text("search.columns")).Class("search-tools__text")),
                [.. Columns.Select(column =>
                {
                    var box = new Input().Attribute("type", "checkbox").Attribute("data-column-toggle", column);
                    if (column is "protocol" or "age" or "title") box.Attribute("disabled", "disabled"); else box.Attribute("data-optional", "");
                    if (column != "files") box.Attribute("checked", "checked");
                    return (IUiComponent)new Label(box, new Span(new Text("search.column." + column))).Class("search-menu__option");
                })],
                "button button--compact button--soft search-tools__columns", "search.columns");
            return new Div(filter, custom, new Span().Class("search-tools__spacer"), sort, columns).Class("search-tools").Attribute("role", "group").TextAttribute("aria-label", "search.tools");
        }

        // The place of the releases while every indexer is asked: the chips and rows of the size the answer takes, never a jump.
        private IUiComponent Skeleton() =>
            new Div(
                new Div([.. _sources.Where(source => Indexers.Length == 0 || Indexers.Contains(source.Id)).Select(source => (IUiComponent)new StateChip(new ValueText(source.Name), UiTone.Neutral, spinning: true))])
                    .Class("search-status").Attribute("aria-hidden", "true"),
                new Div(new TableElement(Head(), new Tbody([.. Enumerable.Range(0, 8).Select(_ => (IUiComponent)new Tr(new Td(new Span().Class("skeleton-block search-skeleton"))
                    .Attribute("colspan", "12")).Class("search-row search-row--skeleton"))])).Class("search-table").Attribute("data-hide", "files")).Class("search-table-wrap"),
                new Div([.. Enumerable.Range(0, 4).Select(_ => (IUiComponent)new Div(new Span().Class("skeleton-block search-skeleton search-skeleton--card")).Class("search-card search-card--skeleton"))])
                    .Class("search-cards"))
                .Class("search-results").Attribute("data-search-results", Address("Results")).Attribute("aria-busy", "true")
                .TextAttribute("data-failed", "deferred.failed").TextAttribute("data-retry", "deferred.retry");

        private IUiComponent Results(IndexerSearchResultV1 result, string? csrf)
        {
            var empty = result.Releases.Count == 0;
            var state = new P(new Text(empty ? "search.empty" : "search.filtered")).Class("search-results__state").Attribute("role", "status").Attribute("data-search-state", "");
            if (empty) state.Attribute("data-empty", ""); else state.Attribute("hidden", "hidden");
            return new Div(
                Chips(result),
                state,
                new Div(new TableElement(Head(), Rows(result, csrf)).Class("search-table").Attribute("data-hide", "files")).Class("search-table-wrap"),
                Cards(result, csrf))
                .Class("search-results").Attribute("data-search-results", "").Attribute("data-loaded", "")
                .Attribute("data-next", result.Indexers.Any(indexer => indexer.Count >= PageSize) ? Address("Results", Offset + PageSize) + "&rows=true" : "");
        }

        // How each indexer answered (Jularr+): its releases and time, or why it gave none.
        private IUiComponent Chips(IndexerSearchResultV1 result) => new Div([.. result.Indexers.Select(indexer => (IUiComponent)(indexer.ErrorKey is { } error
            ? new StateChip(new Span(new ValueText(indexer.Name + " · "), new Text("search.indexer." + error)), error == "category_not_offered" ? UiTone.Neutral : UiTone.Warning)
            : new StateChip(new ValueText(string.Format(CultureInfo.CurrentCulture, "{0} · {1} · {2:0.0} s", indexer.Name, indexer.Count ?? 0, indexer.ElapsedMilliseconds / 1000d)), UiTone.Success)))])
            .Class("search-status").Attribute("role", "status").TextAttribute("aria-label", "search.indexers");

        private IUiComponent Head()
        {
            static IUiComponent Sortable(string column) => new Th(new Button(new Text("search.column." + column)).Attribute("type", "button").Class("search-sort").Attribute("data-sort", column))
                .Attribute("data-col", column).Attribute("aria-sort", column == "age" ? "ascending" : "none");
            var all = new Input().Attribute("type", "checkbox").Attribute("data-select-all", "").TextAttribute("aria-label", "search.selectAll");
            return new Thead(new Tr(new Th(all).Class("search-table__select"), Sortable("protocol"), Sortable("age"), Sortable("title"), Sortable("indexer"), Sortable("size"), Sortable("files"),
                Sortable("grabs"), Sortable("peers"), Sortable("category"), new Th(new Text("search.column.flags")).Attribute("data-col", "flags"), new Th(new Text("common.actions"))));
        }

        private IUiComponent Rows(IndexerSearchResultV1 result, string? csrf) => new Tbody([.. result.Releases.Select(release => Row(release, csrf))]).Attribute("data-search-rows", "");

        private IUiComponent Row(IndexerSearchReleaseV1 release, string? csrf)
        {
            var torrent = release.ReleaseProtocolId == ReleaseProtocol.Torrent;
            IUiComponent title = release.InfoUrl is { } info
                ? new A(new ValueText(release.Title)).Attribute("href", info).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer").Class("search-row__title")
                : new Span(new ValueText(release.Title)).Class("search-row__title");
            return Facts(new Tr(
                new Td(Select(release)).Class("search-table__select"),
                new Td(Protocol(release.ReleaseProtocolId)).Attribute("data-col", "protocol"),
                new Td(Age(release.PublishedAt)).Attribute("data-col", "age"),
                new Td(title, Badges(release)).Attribute("data-col", "title").Class("search-row__release"),
                new Td(new ValueText(release.IndexerName)).Attribute("data-col", "indexer"),
                new Td(new ValueText(ReleaseFacts.Size(release.SizeBytes))).Attribute("data-col", "size"),
                new Td(new ValueText(release.Files is { } files ? Number(files) : "")).Attribute("data-col", "files"),
                new Td(new ValueText(release.Grabs is { } grabs ? grabs.ToString("N0", CultureInfo.CurrentCulture) : "")).Attribute("data-col", "grabs"),
                new Td(torrent ? Peers(release) : new Span(new ValueText("–")).Class("search-row__dim")).Attribute("data-col", "peers"),
                new Td(Category(release)).Attribute("data-col", "category"),
                new Td(release.IndexerFlags.Length == 0 ? new Span() : new Span(new Icon("tag", assets, 14)).Attribute("title", string.Join(", ", release.IndexerFlags)).Class("search-row__flags"))
                    .Attribute("data-col", "flags"),
                new Td(Actions(release, csrf, false)).Class("search-row__actions")).Class("search-row"), release);
        }

        private static IUiComponent Select(IndexerSearchReleaseV1 release) =>
            new Input().Attribute("type", "checkbox").Attribute("data-select", release.ReleaseId).TextAttribute("aria-label", "search.select");

        // A phone shows a card per release with its buttons.
        private IUiComponent Cards(IndexerSearchResultV1 result, string? csrf) => new Div([.. result.Releases.Select(release => Facts(new Div(
            new Div(Select(release), new Span(new ValueText(release.Title)).Class("search-card__title")).Class("search-card__head"),
            Badges(release),
            new Div(Protocol(release.ReleaseProtocolId), new Span(new ValueText(release.IndexerName)), new Span(new ValueText(ReleaseFacts.Size(release.SizeBytes))), Age(release.PublishedAt),
                release.ReleaseProtocolId == ReleaseProtocol.Torrent ? Peers(release) : new Span(), Category(release)).Class("search-card__meta"),
            Actions(release, csrf, true)).Class("search-card"), release))]).Class("search-cards").Attribute("data-search-cards", "");

        // What the browser sorts and filters by.
        private static HtmlElement Facts(HtmlElement element, IndexerSearchReleaseV1 release) => element
            .Attribute("data-key", release.ReleaseId).Attribute("data-title", release.Title).Attribute("data-indexer", release.IndexerName)
            .Attribute("data-protocol", release.ReleaseProtocolId == ReleaseProtocol.Torrent ? "torrent" : "usenet")
            .Attribute("data-published", release.PublishedAt is { } published ? Number(new DateTimeOffset(published, TimeSpan.Zero).ToUnixTimeSeconds()) : "")
            .Attribute("data-size", release.SizeBytes is { } size ? Number(size) : "").Attribute("data-files", release.Files is { } files ? Number(files) : "")
            .Attribute("data-grabs", release.Grabs is { } grabs ? Number(grabs) : "").Attribute("data-seeders", release.Seeders is { } seeders ? Number(seeders) : "")
            .Attribute("data-peers", release.Seeders is { } s && release.Leechers is { } l ? Number(s + l) : "")
            .Attribute("data-category", release.Categories.Length > 0 ? Number(release.Categories[0]) : "")
            .Attribute("data-languages", string.Join(' ', release.Languages)).Attribute("data-quality", release.QualityKey ?? "");

        // What Jularr reads from the name (Jularr+): platform, languages and quality.
        private static IUiComponent Badges(IndexerSearchReleaseV1 release)
        {
            var badges = new List<IUiComponent>();
            if (release.Platform is { } platform) badges.Add(new Span(new ValueText(platform)).Class("search-badge search-badge--platform"));
            badges.AddRange(release.Languages.Select(language => (IUiComponent)new Span(new ValueText(WorkLanguages.Name(WorkLanguages.Code(language)))).Class("search-badge search-badge--language")));
            if (release.QualityKey is { } quality) badges.Add(new Span(new ValueText(quality)).Class("search-badge search-badge--quality"));
            return new Span([.. badges]).Class("search-badges");
        }

        private static IUiComponent Protocol(ReleaseProtocol protocol) => new Span(new Text("search.protocol." + protocol))
            .Class(protocol == ReleaseProtocol.Torrent ? "search-protocol search-protocol--torrent" : "search-protocol search-protocol--usenet");

        // Seeders over leechers; a torrent with few seeders shows in red.
        private static IUiComponent Peers(IndexerSearchReleaseV1 release) => new Span(new ValueText(Number(release.Seeders ?? 0) + " / " + Number(release.Leechers ?? 0)))
            .Class((release.Seeders ?? 0) < 5 ? "search-peers search-peers--low" : "search-peers");

        // An indexer's own sub category is named by its main one with its number ("TV (5170)").
        private static IUiComponent Category(IndexerSearchReleaseV1 release)
        {
            if (release.Categories.FirstOrDefault(NewznabCategories.Known) is var known && known > 0) return new Span(new Text(NewznabCategories.Key(known))).Class("search-category");
            if (release.Categories.Length == 0) return new Span().Class("search-category");
            var own = release.Categories[0];
            return NewznabCategories.Known(own / 1000 * 1000)
                ? new Span(new Text(NewznabCategories.Key(own / 1000 * 1000)), new ValueText(" (" + Number(own) + ")")).Class("search-category")
                : new Span(new ValueText(Number(own))).Class("search-category");
        }

        // The age as Prowlarr shows it (minutes, hours, days), with the moment on hover.
        private IUiComponent Age(DateTime? published)
        {
            if (published is not { } date) return new Span();
            var age = DateTime.UtcNow - date;
            var text = age.TotalMinutes < 60 ? Format("search.age.minutes", Math.Max(0, (int)age.TotalMinutes))
                : age.TotalHours < 24 ? Format("search.age.hours", (int)age.TotalHours) : Format("search.age.days", (int)age.TotalDays);
            return new Span(new ValueText(text)).Attribute("title", date.ToString("u", CultureInfo.InvariantCulture)).Class("search-age");
        }

        // Herunterladen (straight to the downloader), the file and the links of the release (IMDb, TMDB, TVDB, TVmaze).
        private IUiComponent Actions(IndexerSearchReleaseV1 release, string? csrf, bool card)
        {
            var download = new PostForm("/Admin/Search?handler=Download", csrf, [("releaseId", release.ReleaseId), ("returnUrl", Address(null))],
                new Button(new Icon("download", assets, 16), new Span(new Text("search.download")).Class("search-download__text")).Compact().Submit().TextAttribute("title", "search.download"))
                .Attribute("hx-boost", "false").Attribute("data-search-send", "").Class("search-action")
                .TextAttribute("data-failed", "search.download.failed");
            var fileKey = release.ReleaseProtocolId == ReleaseProtocol.Torrent ? "search.save.torrent" : "search.save.nzb";
            var save = new PostForm("/Admin/Search?handler=File", csrf, [("releaseId", release.ReleaseId)],
                new Button(new Icon("document", assets, 16), new Span(new Text(fileKey)).Class("search-action__text")).Compact().Soft().Submit().TextAttribute("title", fileKey))
                .Attribute("hx-boost", "false").Class("search-action");
            var links = new List<IUiComponent>();
            if (release.ImdbId is { } imdb) links.Add(Link("IMDb", "https://www.imdb.com/title/" + Uri.EscapeDataString(imdb) + "/"));
            if (release.TmdbId is { } tmdb) links.Add(Link("TMDB", "https://www.themoviedb.org/" + (release.TvdbId is null && release.TvMazeId is null ? "movie/" : "tv/") + Uri.EscapeDataString(tmdb)));
            if (release.TvdbId is { } tvdb) links.Add(Link("TVDB", "https://thetvdb.com/dereferrer/series/" + Uri.EscapeDataString(tvdb)));
            if (release.TvMazeId is { } tvmaze) links.Add(Link("TVmaze", "https://www.tvmaze.com/shows/" + Uri.EscapeDataString(tvmaze)));
            IUiComponent menu = links.Count == 0 ? new Span().Class("search-action search-action--none")
                : new ActionMenu(new Icon("link", assets, 16), links, "button button--compact button--soft button--icon", "search.links");
            return new Div(PickLink(release), download, save, menu).Class(card ? "search-actions search-actions--card" : "search-actions");
        }

        private static IUiComponent Link(string name, string href) =>
            new A(new ValueText(name)).Class("action-menu__item").Attribute("href", href).Attribute("target", "_blank").Attribute("rel", "noopener noreferrer");

        // The count of releases and of the chosen ones, and the next page of every indexer.
        private IUiComponent Footer() => new Div(
            new Span().Class("search-footer__count").Attribute("data-search-count", "").Attribute("aria-live", "polite")
                .TextAttribute("data-found", "search.found").TextAttribute("data-found-one", "search.found.one").TextAttribute("data-chosen", "search.chosen"),
            new Button(new Icon("plus", assets, 16), new Span(new Text("search.more"))).Soft().Attribute("type", "button").Attribute("data-search-more-button", "").Attribute("disabled", "disabled"),
            new Button(new Icon("download", assets, 16), new Span(new Text("search.grabChosen"))).Primary().Attribute("type", "button").Attribute("data-search-bulk", "").Attribute("disabled", "disabled")
                .TextAttribute("data-done", "search.grabChosen.done"))
            .Class("search-footer").TextAttribute("aria-label", "search.footer");

        private string Format(string key, params object[] values) => string.Format(CultureInfo.CurrentCulture, texts[key], values);

        // "1 indexer" or "3 indexers".
        private string Counted(string key, int count) => count == 1 ? texts[key + ".one"] : Format(key, count);

        private sealed class NoScript(params IUiComponent[] children) : HtmlElement("noscript", children);
    }
}
