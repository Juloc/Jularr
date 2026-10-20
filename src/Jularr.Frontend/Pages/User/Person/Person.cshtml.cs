using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Service.User.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.User.Person
{
    // A person's titles the profile may see, whatever they did for them.
    public sealed class PersonModel(ReadPersonV1 readPerson, ReadPersonWorksV1 readWorks, FrontendAssets assets, IAntiforgery antiforgery) : JularrPage
    {
        private PersonV1? _person;
        private PageResult<WorkCardV1>? _works;

        [BindProperty(SupportsGet = true)]
        public long PersonId { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("person.title", _person?.Name);
            page.Parent("nav.library", "/Library");
            page.Navigation("nav.library");
            page.Asset("Pages/User/Person/Person.entry.scss");
            if (_person is { } person && _works is { } works)
            {
                var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                page.Content.Add(works.Items.Count > 0
                    ? new Div([.. works.Items.Select(item => new MediaCard(assets, item.CanonicalTitle, "/Details/" + item.Id.ToString(CultureInfo.InvariantCulture), item.PosterUrl,
                        item.ReleaseYear?.ToString(CultureInfo.InvariantCulture), MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId))
                        .Bookmark(new MediaBookmark(assets, item.Id, item.IsWatchlisted, Request.Path + Request.QueryString, csrf)))]).Class("media-grid")
                    : new P(new Text("person.empty")));
                if (works.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", Address(works.Page - 1)));
                if (works.Items.Count == works.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", Address(works.Page + 1)));
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
            _person = await readPerson.ExecuteAsync<PersonV1>(new PersonParametersV1(PersonId), HttpContext.RequestAborted);
            _works = await readWorks.ExecuteAsync<PageResult<WorkCardV1>>(new ReadPersonWorksParametersV1(PersonId, Math.Max(1, PageNumber)), HttpContext.RequestAborted);
        }

        private string Address(int page) => "/People/" + PersonId.ToString(CultureInfo.InvariantCulture) + "?pageNumber=" + page.ToString(CultureInfo.InvariantCulture);
    }
}
