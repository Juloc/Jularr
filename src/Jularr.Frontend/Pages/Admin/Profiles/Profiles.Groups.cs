using System.Globalization;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Profiles
{
    public sealed partial class ProfilesModel
    {
        // The release groups, one list for every media type: the tab row carries the rating filter, beside it a live search, the media type and the order. A search that
        // names no group offers to add it; a segmented control rates a group with one click for every profile (the profile names the points of a good or bad group).
        private void DefineGroups(Page page)
        {
            var chips = new List<NavigationTab> { new("common.all", GroupAddress(rating: ""), RatingFilter is null, Icon: Glyph("layers", 14)) };
            chips.AddRange(Enum.GetValues<ReleaseGroupFilter>().Select(filter => new NavigationTab("arr.groups.filter." + filter, GroupAddress(rating: filter.ToString()), RatingFilter == filter)));

            // The search keeps the filter, the media type and the order; each select keeps the others and the words.
            var filters = GroupFilters().ToDictionary(pair => pair.Key, pair => pair.Value);
            var keepWords = new Dictionary<string, string>(filters);
            if (!string.IsNullOrWhiteSpace(Search)) keepWords["q"] = Search.Trim();
            keepWords["tab"] = DownloadPages.Groups;
            filters["tab"] = DownloadPages.Groups;
            var kinds = new List<ChoiceOption> { new("arr.groups.allKinds", "", KindFilter is null) };
            kinds.AddRange(Enum.GetValues<AcquisitionMediaKind>().Select(kind => new ChoiceOption("", kind.ToString(), KindFilter == kind, KindName(kind))));
            var orders = Enum.GetValues<ReleaseGroupSort>().Select(sort => new ChoiceOption("arr.groups.sort." + sort, sort.ToString(), (GroupSort ?? ReleaseGroupSort.Rating) == sort)).ToList();
            // The tabs in their own row like on every tab; below them the rating filter beside the search, the media type and the order.
            page.Content.Add(new ListToolbar(AdminMenu.Row(texts, Request, null, Tab)));
            page.Content.Add(new ListToolbar(new NavigationTabs(chips, "arr.groups.filters", "filters"),
                new SearchBox(Home(Request), "arr.groups.search", Search, filters, null, Glyph("search", 15), live: true),
                new ChoiceSelect(Home(Request), "kind", "arr.groups.kind", kinds, Without(keepWords, "kind")),
                new ChoiceSelect(Home(Request), "sort", "arr.groups.order", orders, Without(keepWords, "sort"))));

            var region = new List<IUiComponent>();
            var words = Search?.Trim();
            var items = _groups?.Items ?? [];
            if (!string.IsNullOrEmpty(words) && words.Length <= 64 && !items.Any(item => string.Equals(item.Name, words, StringComparison.OrdinalIgnoreCase)))
            {
                region.Add(new PostForm(Address("Group"), Token, [("name", words)],
                    new Button(Glyph("plus", 16), new Span(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["arr.groups.addNamed"], words)))).Submit().Class("button button--soft release-groups-add")));
            }

            var filtered = !string.IsNullOrEmpty(words) || RatingFilter is not null || KindFilter is not null;
            var empty = new Div(new IconTile(Glyph("layers", 22), UiTone.Neutral), new P(new Text(filtered ? "arr.groups.noMatch" : "arr.groups.none")).Attribute("role", "status"));
            var columns = new List<ListColumn> { new("arr.groups.name", ListRole.Main), new("arr.groups.rating", ListRole.Actions) };
            var rows = items.Select(item =>
            {
                var seen = item.LastSeenAt is not { } last ? texts["arr.groups.unseen"]
                    : item.SeenCount == 1 ? string.Format(CultureInfo.CurrentCulture, texts["arr.groups.seenOnce"], Age(last))
                    : string.Format(CultureInfo.CurrentCulture, texts["arr.groups.seen"], item.SeenCount.ToString("N0", CultureInfo.CurrentCulture), Age(last));
                var where = (item.ReleaseGroupRatingId is null ? texts["arr.groups.notRated"] + " · " : "")
                    + (item.MediaKinds.Length == 0 ? "" : string.Join(", ", item.MediaKinds.Select(KindName)) + " · ") + seen;
                var identity = new ListIdentity(new IconTile(Glyph("layers", 18), item.ReleaseGroupRatingId switch
                    {
                        ReleaseGroupRating.Good => UiTone.Success,
                        ReleaseGroupRating.Bad => UiTone.Warning,
                        ReleaseGroupRating.Blocked => UiTone.Danger,
                        _ => UiTone.Neutral
                    }), new Span(new ValueText(item.Name)).Class("list-identity__title"), new ValueText(where));
                return new ListRow([identity, RatingForm(item)]);
            }).ToList();
            region.Add(new ListPanel("arr.releaseGroups", columns, rows, empty).Modifier("release-groups-list").Paged(key: "groups", primary: true));
            page.Content.Add(new Region("release-groups", [.. region]).Class("release-groups"));
        }

        // The filter, the media type and the order of the groups tab as the address carries them.
        private IEnumerable<KeyValuePair<string, string>> GroupFilters()
        {
            if (RatingFilter is { } rating) yield return new("rating", rating.ToString());
            if (KindFilter is { } kind) yield return new("kind", kind.ToString());
            if (GroupSort is { } sort and not ReleaseGroupSort.Rating) yield return new("sort", sort.ToString());
        }

        // The groups tab with another rating filter; the search, the media type and the order stay, the list starts on its first page.
        private string GroupAddress(string rating)
        {
            var query = new List<string> { "tab=" + DownloadPages.Groups };
            if (rating.Length > 0) query.Add("rating=" + rating);
            if (KindFilter is { } kind) query.Add("kind=" + kind);
            if (GroupSort is { } sort and not ReleaseGroupSort.Rating) query.Add("sort=" + sort);
            if (!string.IsNullOrWhiteSpace(Search)) query.Add("q=" + Uri.EscapeDataString(Search.Trim()));
            return Home(Request) + "?" + string.Join("&", query);
        }

        private static Dictionary<string, string> Without(Dictionary<string, string> values, string key)
        {
            var copy = new Dictionary<string, string>(values);
            copy.Remove(key);
            return copy;
        }

        // Good, Bad and Blocked as one segmented control; none chosen is not rated. A click saves it.
        private IUiComponent RatingForm(ReleaseGroupDetailsV1 item)
        {
            var form = new Form<RateReleaseGroupDataV1>(form =>
            {
                form.Select(data => data.ReleaseGroupRatingId, "arr.groups.rating",
                    Enum.GetValues<ReleaseGroupRating>().Select(rating => new SelectOption(Number((byte)rating), texts["arr.groups.rating." + rating])), submitOnChange: true);
                var fallback = new Button(new Text("common.save"));
                fallback.Attribute("data-form-submit-fallback", "");
                form.Submit(fallback);
            }, Address("Rate", item.Id, GroupItem), Token, new RateReleaseGroupDataV1(item.ReleaseGroupRatingId));
            return new Div(form).Class("list-actions");
        }

        // How long ago a search last saw the group, in minutes, hours or days.
        private string Age(DateTime lastSeenAt)
        {
            var age = DateTime.UtcNow - DateTime.SpecifyKind(lastSeenAt, DateTimeKind.Utc);
            var (unit, value) = age.TotalHours < 1 ? ("minute", Math.Max(1, (int)age.TotalMinutes)) : age.TotalDays < 1 ? ("hour", (int)age.TotalHours) : ("day", (int)age.TotalDays);
            return value == 1 ? texts["arr.groups.age." + unit] : string.Format(CultureInfo.CurrentCulture, texts["arr.groups.age." + unit + "s"], value);
        }

        public async Task<IActionResult> OnPostGroupAsync(string? name)
        {
            Tab = DownloadPages.Groups;
            _group = new AddReleaseGroupDataV1(name ?? "");
            await addGroup.ExecuteAsync<SavedReleaseGroupV1>(new SaveAcquisitionPolicyParametersV1(), _group, HttpContext.RequestAborted);

            // The list shows the group that was added, ready to be rated.
            Search = name;
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostRateAsync(ReleaseGroupRating? releaseGroupRatingId)
        {
            Tab = DownloadPages.Groups;
            var id = Edit ?? throw new KeyNotFoundException();
            await rateGroup.ExecuteAsync<UpdateResult>(new ReleaseGroupParametersV1(id), new RateReleaseGroupDataV1(releaseGroupRatingId), HttpContext.RequestAborted);
            EditText = null;
            ItemText = null;
            return LocalRedirect(Address());
        }
    }
}
