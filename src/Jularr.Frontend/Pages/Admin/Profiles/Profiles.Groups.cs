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
        // The release groups of the media type: its figures, one field that finds or adds a group, and a list whose segments rate a group for every profile of the type
        // with one click. Not rated groups come first.
        private void DefineGroups(Page page)
        {
            if (_groups is not { } result || _groupFigures is not { } figures) return;

            // A rating updates the figures and the list together, as one region.
            var region = new List<IUiComponent>();
            region.Add(new KpiTiles(
                new KpiTile("arr.groups.total", Count(figures.GroupCount), Glyph("layers"), UiTone.Accent),
                new KpiTile("arr.groups.rating.Good", Count(figures.GoodCount), Glyph("check"), UiTone.Success),
                new KpiTile("arr.groups.notRated", Count(figures.NotRatedCount), Glyph("sliders"), UiTone.Info),
                new KpiTile("arr.groups.rating.Blocked", Count(figures.BlockedCount), Glyph("sync"), UiTone.Warning)).Strip());

            region.Add(new Div(new Form<AddReleaseGroupDataV1>(form =>
            {
                form.Text(item => item.Name, "arr.groups.name", help: new FieldHelp(Placeholder: "arr.groups.find"));
                form.Submit("arr.groups.add");
            }, Address("Group"), Token, _group ?? new AddReleaseGroupDataV1(MediaTypeId, Search ?? ""), FormError)).Class("release-groups-bar"));

            var empty = new Div(new IconTile(Glyph("layers", 22), UiTone.Neutral), new P(new Text("arr.groups.none")).Attribute("role", "status"));
            var columns = new List<ListColumn> { new("arr.groups.name", ListRole.Main), new("arr.groups.rating", ListRole.Actions) };
            var rows = result.Items.Select(item =>
            {
                var seen = item.LastSeenAt is not { } last ? texts["arr.groups.unseen"]
                    : item.SeenCount == 1 ? string.Format(CultureInfo.CurrentCulture, texts["arr.groups.seenOnce"], Age(last))
                    : string.Format(CultureInfo.CurrentCulture, texts["arr.groups.seen"], item.SeenCount.ToString("N0", CultureInfo.CurrentCulture), Age(last));
                var identity = new ListIdentity(new IconTile(Glyph("layers", 18), UiTone.Accent), new Span(new ValueText(item.Name)).Class("list-identity__title"),
                    new ValueText(item.ReleaseGroupRatingId is null ? texts["arr.groups.notRated"] + " · " + seen : seen));
                return new ListRow([identity, RatingForm(item)]);
            }).ToList();
            region.Add(new ListPanel("arr.releaseGroups", columns, rows, empty).Modifier("release-groups-list").Paged());
            page.Content.Add(new Region("release-groups", [.. region]).Class("release-groups"));
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
            }, Address("Rate", item.Id), Token, new RateReleaseGroupDataV1(item.ReleaseGroupRatingId));
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
            _group = new AddReleaseGroupDataV1(MediaTypeId, name ?? "");
            await addGroup.ExecuteAsync<SavedReleaseGroupV1>(new SaveAcquisitionPolicyParametersV1(), _group, HttpContext.RequestAborted);

            // The list shows the group that was found or added, ready to be rated.
            Search = name;
            PageNumber = 1;
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostRateAsync(ReleaseGroupRating? releaseGroupRatingId)
        {
            Tab = DownloadPages.Groups;
            var id = Edit ?? throw new KeyNotFoundException();
            await rateGroup.ExecuteAsync<UpdateResult>(new ReleaseGroupParametersV1(id), new RateReleaseGroupDataV1(releaseGroupRatingId), HttpContext.RequestAborted);
            Edit = null;
            return LocalRedirect(Address());
        }
    }
}
