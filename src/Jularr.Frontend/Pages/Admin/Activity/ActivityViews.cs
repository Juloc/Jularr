using Jularr.Backend.Data.Enums.Media;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Http;

namespace Jularr.Frontend.Pages.Admin.Activity
{
    // What the pages of the Activity entry (Queue, History, Blocked) share: the media types of the type bar, kept in the address `type=a,b` of every view, and the row of the three views.
    public sealed class ActivityViews(IEnumerable<string> requested, UiText texts, HttpRequest request)
    {
        // Every media type is listed in the administration; none asked for shows all of them.
        public ScopeSelection Scopes { get; } = new(requested, [.. LibraryScopes.All.Where(scope => scope.Slug != "all")]);

        // The type bar's choice as the filters a read takes (all types need none).
        public (MediaType[] Types, MediaSubtype[] Subtypes) Filter => Scopes.Filter();

        // The query part of an address for some types: none for all of them.
        private string? Query(IReadOnlyList<string> types) => Scopes.Parameter(types) is { } value ? "type=" + value : null;

        // The path with the chosen types (or the given ones) and whatever else the view adds.
        public string Address(string path, IReadOnlyList<string>? types = null, params string?[] more)
        {
            var query = string.Join("&", new[] { Query(types ?? Scopes.Picked) }.Concat(more).Where(part => !string.IsNullOrEmpty(part)));
            return path + (query.Length > 0 ? "?" + query : "");
        }

        // The entry's pages as one row of chips (Queue, History, Blocked: those the account may open), each keeping the chosen types; counts by page label.
        public NavigationTabs Tabs(IReadOnlyDictionary<string, int>? counts = null) => AdminMenu.Row(texts, request, counts: counts, href: page => Address(page.Path));
    }
}
