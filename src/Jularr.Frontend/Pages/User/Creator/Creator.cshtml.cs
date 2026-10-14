using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Creator
{
    // An author's or artist's titles the profile may see.
    public sealed class CreatorModel(ReadCreatorWorksV1 read) : JularrPage
    {
        private PageResult<WorkCardV1>? _works;

        [BindProperty(SupportsGet = true)]
        public string? Name { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("creator.title");
            page.Navigation("nav.library");
            page.Asset("Pages/User/Creator/Creator.entry.scss");
            if (_works is { } works && Name is { } name)
            {
                page.Content.Add(new H2(new ValueText(name)));
                page.Content.Add(works.Items.Count > 0
                    ? new Div([.. works.Items.Select(item => new MediaCard(item.CanonicalTitle, "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture), item.PosterUrl,
                        item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId)))]).Class("media-grid")
                    : new P(new Text("creator.empty")));
                if (works.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Address(name, works.Page - 1)));
                if (works.Items.Count == works.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", Address(name, works.Page + 1)));
            }

            if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            _works = await read.ExecuteAsync<PageResult<WorkCardV1>>(new ReadCreatorWorksParametersV1(Name ?? "", Math.Max(1, PageNumber)), HttpContext.RequestAborted);
        }

        private static string Address(string name, int page) => "/Creator?name=" + Uri.EscapeDataString(name) + "&pageNumber=" + page.ToString(CultureInfo.InvariantCulture);
    }
}
