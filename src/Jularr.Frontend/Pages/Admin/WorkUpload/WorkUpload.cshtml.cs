using System.Globalization;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.WorkUpload
{
    // Only this page accepts a request as large as a volume.
    [RequestSizeLimit(UploadFields.VolumeRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadFields.VolumeRequestBytes)]
    public sealed class WorkUploadModel(UploadVolumeV1 upload, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        protected override void Define(Page page)
        {
            page.Title("work.upload");
            page.Navigation("nav.admin");
            page.Content.Add(new A(new Text("admin.work.title")).Attribute("href", WorkAddress));
            page.Content.Add(new Form<UploadVolumeDataV1>(form =>
            {
                form.File(item => item.File, "work.upload.file");
                form.Number(item => item.VolumeNumber, "work.upload.volume");
                form.Submit("work.upload.submit");
            }, WorkAddress + "/Upload", antiforgery.GetAndStoreTokens(HttpContext).RequestToken, null, FormError));
        }

        public IActionResult OnGet()
        {
            if (!texts.CanUploadVolumes())
            {
                return Forbid();
            }

            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(IFormFile? file, decimal? volumeNumber)
        {
            var data = new UploadVolumeDataV1(file is null ? null : new UploadedFile(file.FileName, file.Length, file.OpenReadStream), volumeNumber);
            await upload.ExecuteAsync<UpdateResult>(new WorkParametersV1(Id), data, HttpContext.RequestAborted);
            Hint("work.upload.done");
            return LocalRedirect(WorkAddress);
        }

        private string WorkAddress => "/Admin/Works/" + Id.ToString(CultureInfo.InvariantCulture);
    }
}
