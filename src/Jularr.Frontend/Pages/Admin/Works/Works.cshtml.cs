using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Works
{
    public sealed class WorksModel(ReadWorksV1 read, CreateWorkV1 create, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private PageResult<WorkDetailsV1>? _works;
        private CreateWorkDataV1? _input;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("admin.works.title");
            page.Navigation("nav.admin");

            // Manual canonical creation.
            var mediaTypes = Enum.GetValues<MediaType>().Select(type => new SelectOption(((byte)type).ToString(CultureInfo.InvariantCulture), texts["media.type." + type]));
            page.Content.Add(new Form<CreateWorkDataV1>(form =>
            {
                form.Select(item => item.MediaTypeId, "media.type", mediaTypes);
                form.Text(item => item.CanonicalTitle, "media.title");
                form.Number(item => item.ReleaseYear, "media.year");
                form.Submit("media.create");
            }, "/Admin/Works", antiforgery.GetAndStoreTokens(HttpContext).RequestToken, _input, Error));

            // Real catalog rows.
            if (_works is { } works)
            {
                page.Content.Add(new Table<WorkDetailsV1>(works.Items)
                    .Text(item => item.CanonicalTitle, "media.title")
                    .Resource(item => item.MediaTypeId, "media.type", type => "media.type." + type)
                    .Link(item => item.Id, "common.open", id => "/Admin/Works/" + id.ToString(CultureInfo.InvariantCulture), "common.open"));

                if (works.Items.Count == 0) page.Content.Add(new P(new Text("media.empty")));
                if (works.Page > 1) page.Content.Add(new A(new Text("common.previous")).Attribute("href", "/Admin/Works?pageNumber=" + (works.Page - 1)));
                if (works.Items.Count == works.PageSize) page.Content.Add(new A(new Text("common.next")).Attribute("href", "/Admin/Works?pageNumber=" + (works.Page + 1)));
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] MediaType mediaTypeId, string? canonicalTitle, int? releaseYear)
        {
            _input = new CreateWorkDataV1(mediaTypeId, canonicalTitle ?? "", releaseYear);
            var work = await create.ExecuteAsync<WorkDetailsV1>(_input, HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Work/Work", new { id = work.Id });
        }

        protected override async Task LoadDataAsync()
        {
            _works = await read.ExecuteAsync<PageResult<WorkDetailsV1>>(new ReadWorksParametersV1(PageNumber), HttpContext.RequestAborted);
        }
    }
}
