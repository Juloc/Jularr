using System.Globalization;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Work
{
    public sealed class WorkModel(ReadWorkV1 read, UpdateWorkV1 update, RefreshWorkMetadataV1 refresh, IAntiforgery antiforgery, UiText texts) : JularrPage
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
                SelectOption[] subtypes = [new SelectOption("", texts["media.subtype.none"]), .. Enum.GetValues<MediaSubtype>().Where(subtype => MediaSubtypes.Fits(work.MediaTypeId, subtype))
                    .Select(subtype => new SelectOption(((byte)subtype).ToString(CultureInfo.InvariantCulture), texts["media.subtype." + subtype]))];
                page.Content.Add(new Form<UpdateWorkDataV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Text(item => item.CanonicalTitle, "media.title");
                    form.Number(item => item.ReleaseYear, "media.year");
                    if (subtypes.Length > 1) form.Select(item => item.MediaSubtypeId, "media.subtype", subtypes);
                    form.Submit("common.save");
                }, "/Admin/Works/" + work.Id.ToString(CultureInfo.InvariantCulture), antiforgery.GetAndStoreTokens(HttpContext).RequestToken,
                    _input ?? new UpdateWorkDataV1(work.CanonicalTitle, work.Version, work.ReleaseYear, work.MediaSubtypeId), _input is null ? null : FormError));
                page.Content.Add(new Form<WorkParametersV1>(form => form.Submit("admin.work.refreshMetadata"),
                    "/Admin/Works/" + work.Id.ToString(CultureInfo.InvariantCulture) + "?handler=Refresh", antiforgery.GetAndStoreTokens(HttpContext).RequestToken, new WorkParametersV1(work.Id)));
                if (_input is null && FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
            }
            else if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string? canonicalTitle, long version, int? releaseYear, MediaSubtype? mediaSubtypeId)
        {
            _input = new UpdateWorkDataV1(canonicalTitle ?? "", version, releaseYear, mediaSubtypeId);
            await update.ExecuteAsync<UpdateResult>(new WorkParametersV1(Id), _input, HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Work/Work", new { id = Id });
        }

        public async Task<IActionResult> OnPostRefreshAsync()
        {
            await refresh.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(Id), default, HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Work/Work", new { id = Id });
        }

        protected override async Task LoadDataAsync()
        {
            _work = await read.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(Id), HttpContext.RequestAborted);
        }
    }
}
