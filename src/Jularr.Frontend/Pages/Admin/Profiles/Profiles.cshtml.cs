using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Profiles
{
    public sealed class ProfilesModel(ReadAcquisitionProfilesV1 profiles, CreateAcquisitionProfileV1 create, UpdateAcquisitionProfileV1 update, DeleteAcquisitionProfileV1 delete,
        IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        // Media types with release qualities.
        private static readonly MediaType[] MediaTypes = [MediaType.Movie, MediaType.Series, MediaType.Book, MediaType.LightNovel];

        private IReadOnlyList<AcquisitionProfileDetailsV1> _profiles = [];
        private AcquisitionProfileDetailsV1? _selected;
        private SaveAcquisitionProfileDataV1? _input;

        [BindProperty(SupportsGet = true)]
        public long? AcquisitionProfileId { get; set; }

        [BindProperty(SupportsGet = true)]
        public MediaType? NewFor { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.profiles.title");
            page.Navigation("nav.admin");
            page.Content.Add(AdminNavigation.Acquisition);
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var editing = _selected is not null || NewFor is not null;

            page.Content.Add(new Split(split =>
            {
                split.Primary.Add(new Table<AcquisitionProfileDetailsV1>(_profiles)
                    .Text(item => item.Name, "profile.name")
                    .Resource(item => item.MediaTypeId, "table.type", type => "media.type." + type)
                    .Content("profile.qualities", item => new ValueText(string.Join(", ", item.AllowedQualities)))
                    .Content("profile.usage", item => item.IsDefault ? new Text("profile.default") : new ValueText(item.WantedCount.ToString(CultureInfo.InvariantCulture)))
                    .Link(item => item.Id, "common.actions", id => Address(id), "common.edit"));
                foreach (var type in MediaTypes)
                {
                    split.Primary.Add(new A(new Text("profile.add." + type)).Class("button").Attribute("href", Address(newFor: type)));
                }

                if (editing)
                {
                    split.Secondary.Add(ProfileForm(csrf));
                    if (_selected is { IsDefault: false } selected)
                    {
                        split.Secondary.Add(new Form<DeleteAcquisitionProfileParametersV1>(form =>
                        {
                            form.Hidden(item => item.Version);
                            form.Submit("profile.delete");
                        }, Address(selected.Id, handler: "Delete"), csrf, new DeleteAcquisitionProfileParametersV1(selected.Id, selected.Version)));
                    }
                }
            }).MasterDetail(editing, Address()));

            if (Error is not null && _input is null) page.Content.Add(new P(new Text(Error.ResourceKey)).Attribute("role", "alert"));
        }

        private Form<SaveAcquisitionProfileDataV1> ProfileForm(string? csrf)
        {
            var input = _input ?? (_selected is { } selected
                ? new SaveAcquisitionProfileDataV1(selected.Name, selected.MediaTypeId, selected.IsDefault, selected.AllowedQualities, selected.UpgradeAllowed, selected.UpgradeCutoffQuality,
                    selected.MinimumSizeMb, selected.MaximumSizeMb, selected.MustContain, selected.MustNotContain, selected.Version)
                : NewProfile(NewFor!.Value));
            var qualities = ProfileQualities.For(input.MediaTypeId).Select(quality => new SelectOption(quality, quality)).ToArray();

            return new Form<SaveAcquisitionProfileDataV1>(form =>
            {
                form.Hidden(item => item.MediaTypeId);
                form.Hidden(item => item.Version);
                form.Text(item => item.Name, "profile.name");
                form.Select(item => item.IsDefault, "profile.isDefault", YesNo());
                form.Choices(item => item.AllowedQualities, "profile.qualities", qualities);
                form.Select(item => item.UpgradeAllowed, "profile.upgradeAllowed", YesNo());
                form.Select(item => item.UpgradeCutoffQuality, "profile.cutoff", [new SelectOption("", texts["profile.noCutoff"]), .. qualities]);
                form.Number(item => item.MinimumSizeMb, "profile.minimumSize");
                form.Number(item => item.MaximumSizeMb, "profile.maximumSize");
                form.Text(item => item.MustContain, "profile.mustContain");
                form.Text(item => item.MustNotContain, "profile.mustNotContain");
                form.Submit("common.save");
            }, Address(AcquisitionProfileId, NewFor), csrf, input, _input is null ? null : Error);
        }

        // Sensible starting points per kind.
        private static SaveAcquisitionProfileDataV1 NewProfile(MediaType mediaType) => mediaType is MediaType.Movie or MediaType.Series
            ? new SaveAcquisitionProfileDataV1("", mediaType, false, ["WEB-1080p", "BLURAY-1080p"], true, "WEB-1080p", null, null, [], [])
            : new SaveAcquisitionProfileDataV1("", mediaType, false, ["EPUB"], false, null, null, null, [], []);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] MediaType mediaTypeId, string? name, [BindRequired] bool isDefault, string[]? allowedQualities, [BindRequired] bool upgradeAllowed,
            string? upgradeCutoffQuality, int? minimumSizeMb, int? maximumSizeMb, string? mustContain, string? mustNotContain, long? version)
        {
            _input = new SaveAcquisitionProfileDataV1(name ?? "", mediaTypeId, isDefault, allowedQualities ?? [], upgradeAllowed, Blank(upgradeCutoffQuality), minimumSizeMb, maximumSizeMb,
                Terms(mustContain), Terms(mustNotContain), version);
            if (AcquisitionProfileId is { } existing)
            {
                await update.ExecuteAsync<UpdateResult>(new AcquisitionProfileParametersV1(existing), _input, HttpContext.RequestAborted);
            }
            else
            {
                AcquisitionProfileId = (await create.ExecuteAsync<CreateAcquisitionProfileResultV1>(new CreateAcquisitionProfileParametersV1(), _input, HttpContext.RequestAborted)).Id;
            }

            return LocalRedirect(Address(AcquisitionProfileId));
        }

        public async Task<IActionResult> OnPostDeleteAsync([BindRequired] long version)
        {
            var id = AcquisitionProfileId ?? throw new KeyNotFoundException("No profile was selected.");
            await delete.ExecuteAsync<UpdateResult>(new DeleteAcquisitionProfileParametersV1(id, version), default, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        protected override async Task LoadDataAsync()
        {
            _profiles = await profiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(), HttpContext.RequestAborted);
            _selected = AcquisitionProfileId is { } id ? _profiles.FirstOrDefault(profile => profile.Id == id) ?? throw new KeyNotFoundException("The profile was not found.") : null;
            if (NewFor is { } type && !MediaTypes.Contains(type)) NewFor = null;
        }

        private SelectOption[] YesNo() => [new SelectOption("True", texts["common.yes"]), new SelectOption("False", texts["common.no"])];

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        private static string[] Terms(string? value) => value is null ? [] : [.. value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

        private static string Address(long? id = null, MediaType? newFor = null, string? handler = null)
        {
            var address = "/Admin/Acquisition/Profiles";
            if (id is { } profileId) address += "?AcquisitionProfileId=" + profileId.ToString(CultureInfo.InvariantCulture);
            else if (newFor is { } type) address += "?NewFor=" + type;
            return handler is null ? address : address + (address.Contains('?') ? "&" : "?") + "handler=" + handler;
        }
    }
}
