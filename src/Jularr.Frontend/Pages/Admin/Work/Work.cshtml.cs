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
    public sealed class WorkModel(ReadWorkV1 read, UpdateWorkV1 update, RefreshWorkMetadataV1 refresh, ReadWorkUnitGroupsV1 readGroups, CreateWorkUnitGroupV1 createGroup,
        DeleteWorkUnitGroupV1 deleteGroup, SearchMetadataV1 search, RematchWorkV1 rematch, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private SearchMetadataDataV1? _matchQuery;
        private IReadOnlyList<MetadataSearchResultV1>? _matches;
        private WorkDetailsV1? _work;
        private UpdateWorkDataV1? _input;
        private IReadOnlyList<WorkUnitGroupV1>? _groups;
        private CreateWorkUnitGroupDataV1? _groupInput;

        [BindProperty(SupportsGet = true)]
        public long Id { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.work.title", _work?.CanonicalTitle);
            page.Navigation("nav.admin");

            // Its searches, episodes and releases live with its Arr+ target.
            page.Content.Add(new Div([new A(new Text("admin.works.title")).Class("button").Attribute("href", "/Admin/Works"),
                .. _work?.WantedItemId is { } wanted && texts.CanManageWanted()
                    ? new IUiComponent[] { new A(new Text("admin.work.wanted")).Class("button").Attribute("href", "/Admin/Wanted/" + wanted.ToString(CultureInfo.InvariantCulture)) }
                    : []]).Class("page-actions"));

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
                    page.Content.Add(new A(new Text("work.upload")).Class("button").Attribute("href", Home + "/Upload"));
                    if (TempData["VolumeUploaded"] is true) page.Content.Add(new P(new Text("work.upload.done")).Attribute("role", "status"));
                }

                if (_input is null && _groupInput is null && _matchQuery is null && FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
                DefineMatch(page, work);

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

        // A wrong match is replaced by another provider entry; files, requests and manual edits stay with the Work.
        private void DefineMatch(Page page, WorkDetailsV1 work)
        {
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var content = new List<IUiComponent>
            {
                new Form<SearchMetadataDataV1>(form =>
                {
                    form.Hidden(item => item.MediaTypeId);
                    form.Hidden(item => item.MediaSubtypeId);
                    form.Text(item => item.Query, "media.title");
                    form.Submit("admin.works.searchMetadata");
                }, Home + "?handler=MatchSearch", csrf, _matchQuery ?? new SearchMetadataDataV1(work.MediaTypeId, work.CanonicalTitle, null, work.MediaSubtypeId),
                    _matchQuery is null ? null : FormError)
            };
            if (_matches is { } matches)
            {
                content.Add(matches.Count == 0 ? new P(new Text("discover.noResults")).Attribute("role", "status") : new Table<MetadataSearchResultV1>(matches)
                    .Text(item => item.Title, "media.title")
                    .Content("media.year", item => new ValueText(item.ReleaseYear?.ToString(CultureInfo.InvariantCulture) ?? ""))
                    .Content("media.author", item => new ValueText(item.Author ?? ""))
                    .Content("common.actions", item => item.WorkId == work.Id ? new Text("admin.work.currentMatch") : new Form<RematchWorkDataV1>(form =>
                    {
                        form.Hidden(data => data.Namespace);
                        form.Hidden(data => data.ExternalId);
                        form.Hidden(data => data.Title);
                        form.Hidden(data => data.ReleaseYear);
                        form.Hidden(data => data.Version);
                        form.Submit("admin.work.useMatch", item.Title, valueHidden: true);
                    }, Home + "?handler=Match", csrf, new RematchWorkDataV1(item.Namespace, item.ExternalId, item.Title, item.ReleaseYear, work.Version))));
            }

            page.Content.Add(new UiSection("admin.work.match", [.. content]));
        }

        public async Task<IActionResult> OnPostMatchSearchAsync([BindRequired] MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string? query)
        {
            _matchQuery = new SearchMetadataDataV1(mediaTypeId, query ?? "", null, mediaSubtypeId);
            await LoadDataAsync();
            _matches = await search.ExecuteAsync<BaseDataList<MetadataSearchResultV1>>(new SearchMetadataParametersV1(), _matchQuery, HttpContext.RequestAborted);
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostMatchAsync([BindRequired] string @namespace, [BindRequired] string externalId, [BindRequired] string title, int? releaseYear, [BindRequired] long version)
        {
            await rematch.ExecuteAsync<WorkDetailsV1>(new WorkParametersV1(Id), new RematchWorkDataV1(@namespace, externalId, title, releaseYear, version), HttpContext.RequestAborted);
            return RedirectToPage("/Admin/Work/Work", new { id = Id });
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
