using System.Globalization;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Work
{
    [RequestSizeLimit(2_001_048_576)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2_001_048_576)]
    public sealed class WorkModel(ReadWorkV1 read, UpdateWorkV1 update, RefreshWorkMetadataV1 refresh, ReadWorkUnitGroupsV1 readGroups, CreateWorkUnitGroupV1 createGroup,
        DeleteWorkUnitGroupV1 deleteGroup, UploadVolumeV1 upload, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private bool _uploading;
        private WorkDetailsV1? _work;
        private UpdateWorkDataV1? _input;
        private IReadOnlyList<WorkUnitGroupV1>? _groups;
        private CreateWorkUnitGroupDataV1? _groupInput;

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
                // A manga volume from the Admin's own device.
                if (work.MediaTypeId == MediaType.Book && work.MediaSubtypeId == MediaSubtype.Manga && texts.CanUploadVolumes())
                {
                    page.Content.Add(new UiSection("work.upload", [new Form<UploadVolumeDataV1>(form =>
                    {
                        form.File(item => item.File, "work.upload.file");
                        form.Number(item => item.VolumeNumber, "work.upload.volume");
                        form.Submit("work.upload.submit");
                    }, Home + "?handler=Upload", antiforgery.GetAndStoreTokens(HttpContext).RequestToken, null, _uploading ? FormError : null)]));
                    if (TempData["VolumeUploaded"] is true) page.Content.Add(new P(new Text("work.upload.done")).Attribute("role", "status"));
                }

                if (_input is null && _groupInput is null && !_uploading && FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));

                // Named sections over the units; units and files stay as they are.
                var units = WorkUnitTypes.For(work.MediaTypeId);
                if (_groups is { } groups && units.Count > 0)
                {
                    var unit = units[0];
                    var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
                    var hasParent = WorkUnitTypes.ParentOf(unit) is not null;
                    var content = new List<IUiComponent>();
                    if (groups.Count > 0)
                    {
                        content.Add(new Table<WorkUnitGroupV1>(groups)
                            .Text(item => item.Name, "unitGroups.name")
                            .Content("unitGroups.from", item => new ValueText(MediaUnitLabel.Format(texts, unit, item.FirstNumber, item.FirstParentNumber) ?? ""))
                            .Content("unitGroups.to", item => new ValueText(MediaUnitLabel.Format(texts, unit, item.LastNumber, item.LastParentNumber) ?? ""))
                            .Content("common.actions", item => new Form<WorkUnitGroupParametersV1>(form => form.Submit("unitGroups.delete"),
                                Home + "?handler=DeleteGroup&workUnitGroupId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf, new WorkUnitGroupParametersV1(work.Id, item.Id))));
                    }

                    content.Add(new Form<CreateWorkUnitGroupDataV1>(form =>
                    {
                        form.Text(item => item.Name, "unitGroups.name");
                        if (hasParent) form.Number(item => item.FirstParentNumber, "unitGroups.fromParent");
                        form.Number(item => item.FirstNumber, "unitGroups.from");
                        if (hasParent) form.Number(item => item.LastParentNumber, "unitGroups.toParent");
                        form.Number(item => item.LastNumber, "unitGroups.to");
                        form.Submit("unitGroups.add");
                    }, Home + "?handler=Group", csrf, _groupInput, _groupInput is null ? null : FormError));
                    page.Content.Add(new UiSection("unitGroups.title", [.. content]));
                }
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

        public async Task<IActionResult> OnPostGroupAsync(string? name, decimal? firstParentNumber, [BindRequired] decimal firstNumber, decimal? lastParentNumber, [BindRequired] decimal lastNumber)
        {
            _groupInput = new CreateWorkUnitGroupDataV1(name ?? "", firstParentNumber, firstNumber, lastParentNumber, lastNumber);
            await createGroup.ExecuteAsync<CreateWorkUnitGroupResultV1>(new WorkParametersV1(Id), _groupInput, HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Work/Work", new { id = Id });
        }

        public async Task<IActionResult> OnPostDeleteGroupAsync([BindRequired] long workUnitGroupId)
        {
            await deleteGroup.ExecuteAsync<UpdateResult>(new WorkUnitGroupParametersV1(Id, workUnitGroupId), default, HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Work/Work", new { id = Id });
        }

        public async Task<IActionResult> OnPostUploadAsync(IFormFile? file, decimal? volumeNumber)
        {
            _uploading = true;
            var data = new UploadVolumeDataV1(file is null ? null! : new UploadedFile(file.FileName, file.Length, file.OpenReadStream), volumeNumber);
            await upload.ExecuteAsync<UpdateResult>(new WorkParametersV1(Id), data, HttpContext.RequestAborted);
            TempData["VolumeUploaded"] = true;
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostRefreshAsync()
        {
            await refresh.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(Id), default, HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Work/Work", new { id = Id });
        }

        protected override async Task LoadDataAsync()
        {
            _work = await read.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(Id), HttpContext.RequestAborted);
            if (texts.CanGroupUnits())
            {
                _groups = await readGroups.ExecuteAsync<BaseDataList<WorkUnitGroupV1>>(new WorkParametersV1(Id), HttpContext.RequestAborted);
            }
        }

        private string Home => "/Admin/Works/" + Id.ToString(CultureInfo.InvariantCulture);
    }
}
