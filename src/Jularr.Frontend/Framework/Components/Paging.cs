using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Jularr.Frontend.Framework
{
    // The one paging model of every paged list. A page is up to PageSize rows, read without counting and without a look ahead; the label says "Page 2".
    // Next is offered when the page is full (there is probably another; an empty one shows the list's empty state), Previous from page 2 on.
    public sealed record DockPaging(string? PreviousHref, string? NextHref, IUiComponent Label)
    {
        public static bool HasPrevious(int page) => page > 1;

        public static bool HasNext(int rows, int pageSize) => rows >= pageSize;

        // A short first page needs no paging; otherwise the pager shows (an empty page behind the last one still leads back).
        public static DockPaging? Of(int page, int rows, int pageSize, Func<int, string> address) =>
            HasPrevious(page) || HasNext(rows, pageSize)
                ? new DockPaging(HasPrevious(page) ? address(page - 1) : null, HasNext(rows, pageSize) ? address(page + 1) : null, new PageLabel(page))
                : null;
    }

    // "Page 2" in the person's language.
    public sealed class PageLabel(int page) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            encoder.Encode(writer, string.Format(CultureInfo.CurrentCulture, texts["dock.pageNumber"], page));
    }

    // Paging is an option of the list: `.Paged()` on a Table or ListPanel, `.Paged(rows)` on anything else. A page with several paged lists gives each its own
    // query key (`.Paged(key: "files")` pages `?files=2`), the others keep their page in the address. The pager of the page's primary list (the first, or the one
    // marked `primary: true`) sits in the floating dock; every other list draws a small pager under itself. The page never wires a dock for it.
    public sealed class PagedList
    {
        public const string DefaultKey = "pageNumber";

        private readonly PagingScope? _scope;

        internal PagedList(PagingScope? scope, int rows, int pageSize, string key, bool primary)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
            _scope = scope;
            Rows = rows;
            PageSize = pageSize;
            Key = key;
            IsPrimary = primary;
            Page = scope is null ? 1 : PagingScope.PageOf(scope.Request.Query, key);
        }

        public int Page { get; }
        public int Rows { get; }
        public int PageSize { get; }
        public string Key { get; }
        public bool IsPrimary { get; }

        // Set when the pager sits in the page's dock.
        public bool IsDocked { get; internal set; }

        public DockPaging? Paging => _scope is null ? null : DockPaging.Of(Page, Rows, PageSize, number => _scope.Link(Key, number));

        // The small pager under a list that is not the page's primary one.
        public void WriteInline(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (!IsDocked && _scope is not null && Paging is { } paging) ActionDock.PagingZone(_scope.Assets, paging, inline: true).WriteTo(writer, encoder, texts);
        }
    }

    // What a page definition knows while it is built: the request (its query holds every list's page) and the paged lists in the order they were declared.
    public sealed class PagingScope
    {
        private static readonly AsyncLocal<PagingScope?> Active = new();
        private readonly List<PagedList> _lists = [];
        private PagingScope? _previous;

        private PagingScope(HttpRequest request, FrontendAssets assets)
        {
            Request = request;
            Assets = assets;
        }

        public HttpRequest Request { get; }
        public FrontendAssets Assets { get; }
        public IReadOnlyList<PagedList> Lists => _lists;

        public static PagingScope? Current => Active.Value;

        public static PagingScope Begin(HttpRequest request, FrontendAssets assets)
        {
            var scope = new PagingScope(request, assets) { _previous = Active.Value };
            Active.Value = scope;
            return scope;
        }

        public static void End() => Active.Value = Active.Value?._previous;

        internal static PagedList Register(int rows, int pageSize, string key, bool primary)
        {
            var scope = Current;
            var list = new PagedList(scope, rows, pageSize, key, primary);
            scope?._lists.Add(list);
            return list;
        }

        // The pager of the primary list goes into the page's dock (the page's own, else a dock of its own).
        public void Attach(Page page)
        {
            // The reader and the player have no dock; their pagers stay in place.
            if (page.IsReader || page.IsPlayer) return;
            var primary = _lists.FirstOrDefault(list => list.IsPrimary) ?? _lists.FirstOrDefault();
            if (primary?.Paging is not { } paging) return;
            primary.IsDocked = true;
            var dock = page.Content.OfType<ActionDock>().FirstOrDefault(item => !item.IsForm);
            if (dock is null)
            {
                dock = new ActionDock(Assets, "dock.paging");
                page.Content.Add(dock);
            }

            dock.AddPaging(Assets, paging);
        }

        // The page's current address with this list's page set and everything else (the other lists' pages, the filters) kept.
        public string Link(string key, int number) => Link(Request.PathBase + Request.Path, Request.Query, key, number);

        public static string Link(string path, IEnumerable<KeyValuePair<string, StringValues>> query, string key, int number)
        {
            var kept = new List<KeyValuePair<string, string?>>();
            foreach (var (name, values) in query)
            {
                if (name.Equals(key, StringComparison.OrdinalIgnoreCase) || name.Equals("handler", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var value in values) kept.Add(new(name, value));
            }

            if (number > 1) kept.Add(new(key, number.ToString(CultureInfo.InvariantCulture)));
            return path + QueryString.Create(kept);
        }

        public static int PageOf(IEnumerable<KeyValuePair<string, StringValues>> query, string key) =>
            query.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value.ToString() is { Length: > 0 } text
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page > 0 ? page : 1;
    }

    // Paging for a list that is not a Table or ListPanel (a grid of cards, a short list): the content with its pager.
    public static class PagedExtensions
    {
        public static IUiComponent Paged(this IUiComponent content, int rows, int pageSize = 25, string key = PagedList.DefaultKey, bool primary = false) =>
            new PagedBlock(content, PagingScope.Register(rows, pageSize, key, primary));

        private sealed class PagedBlock(IUiComponent content, PagedList list) : IUiComponent
        {
            public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
            {
                content.WriteTo(writer, encoder, texts);
                list.WriteInline(writer, encoder, texts);
            }
        }
    }
}
