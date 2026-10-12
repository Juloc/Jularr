using System.Globalization;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Work
{
    public sealed class WorkModel(ReadWorkV1 read, UpdateWorkV1 update, IAntiforgery antiforgery) : JularrPage
    {
        private WorkDetailsV1? _work;
        private UpdateWorkDataV1? _input;

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.work.title");
            page.Navigation("nav.admin");

            page.Content.Add(new A(new Text("admin.works.title")).Attribute("href", "/Admin/Works"));

            // Edit real identity metadata.
            if (_work is { } work)
            {
                page.Content.Add(new P(new Text("media.type." + work.MediaTypeId)));
                page.Content.Add(new Form<UpdateWorkDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Text(item => item.CanonicalTitle, "media.title");
                    form.Number(item => item.ReleaseYear, "media.year");
                    form.Submit("common.save");
                }, "/Admin/Works/" + work.Id.ToString(CultureInfo.InvariantCulture), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _input ?? new UpdateWorkDataV1(work.CanonicalTitle, work.Version, work.ReleaseYear), Error));
            }
            else if (Error is not null) page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? canonicalTitle, long version, int? releaseYear)
        {
            _input = new UpdateWorkDataV1(canonicalTitle ?? "", version, releaseYear);
            await update.ExecuteAsync<UpdateResult>(new WorkParametersV1(Id), _input, HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Work/Work", new { id = Id });
        }

        protected override async Task LoadDataAsync()
        {
            _work = await read.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(Id), HttpContext.RequestAborted);
        }
    }
}
