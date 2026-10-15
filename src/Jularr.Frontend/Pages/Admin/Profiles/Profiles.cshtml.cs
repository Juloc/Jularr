using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Profiles
{
    public sealed class ProfilesModel(ReadAcquisitionProfilesV1 profiles, CreateAcquisitionProfileV1 create, UpdateAcquisitionProfileV1 update, DeleteAcquisitionProfileV1 delete,
        ReadAcquisitionProfileRulesV1 readRules, AddAcquisitionProfileRuleV1 addRule, DeleteAcquisitionProfileRuleV1 deleteRule, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        // Media types with release qualities.
        private static readonly (MediaType Type, MediaSubtype? Subtype)[] Kinds = [(MediaType.Movie, null), (MediaType.Series, null), (MediaType.Book, null), (MediaType.Book, MediaSubtype.LightNovel)];

        private IReadOnlyList<AcquisitionProfileDetailsV1> _profiles = [];
        private AcquisitionProfileDetailsV1? _selected;
        private SaveAcquisitionProfileDataV1? _input;
        private AddReleaseRuleDataV1? _ruleInput;
        private IReadOnlyList<ReleaseRuleV1> _rules = [];

        [BindProperty(SupportsGet = true)]
        public long? AcquisitionProfileId { get; set; }

        [BindProperty(SupportsGet = true)]
        public MediaType? NewFor { get; set; }

        [BindProperty(SupportsGet = true)]
        public MediaSubtype? NewForSubtype { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.profiles.title");
            page.Navigation("nav.admin");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            if (SetupWizard.IsActive(Request)) page.Content.Add(SetupWizard.Frame(SetupStepType.Profiles, csrf));
            var editing = _selected is not null || NewFor is not null;

            page.Content.Add(new Split(split =>
            {
                split.Primary.Add(new Table<AcquisitionProfileDetailsV1>(_profiles)
                    .Text(item => item.Name, "profile.name")
                    .Content("table.type", item => new Text(MediaTypeLabel.Key(item.MediaTypeId, item.MediaSubtypeId)))
                    .Content("profile.qualities", item => new ValueText(string.Join(", ", item.AllowedQualities)))
                    .Content("profile.usage", item => item.IsDefault ? new Text("profile.default") : new ValueText(item.WantedCount.ToString(CultureInfo.InvariantCulture)))
                    .Link(item => item.Id, "common.actions", id => Address(id), "common.edit"));
                foreach (var (type, subtype) in Kinds)
                {
                    split.Primary.Add(new A(new Text("profile.add." + (subtype?.ToString() ?? type.ToString()))).Class("button").Attribute("href", Address(newFor: type, newForSubtype: subtype)));
                }

                if (editing)
                {
                    split.Secondary.Add(ProfileForm(csrf));
                    if (_selected is { } edited)
                    {
                        DefineRules(split.Secondary, edited, csrf);
                    }

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

            if (FormError is not null && _input is null && _ruleInput is null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
        }

        private Form<SaveAcquisitionProfileDataV1> ProfileForm(string? csrf)
        {
            var input = _input ?? (_selected is { } selected
                ? new SaveAcquisitionProfileDataV1(selected.Name, selected.MediaTypeId, selected.IsDefault, selected.AllowedQualities, selected.UpgradeAllowed, selected.UpgradeCutoffQuality,
                    selected.MinimumSizeMb, selected.MaximumSizeMb, selected.MustContain, selected.MustNotContain, selected.Version, selected.MediaSubtypeId, selected.DelayMinutes,
                    selected.FallbackQualities, selected.FallbackAfterHours)
                : NewProfile(NewFor!.Value, NewForSubtype));
            var qualities = ProfileQualities.For(input.MediaTypeId, input.MediaSubtypeId).Select(quality => new SelectOption(quality, quality)).ToArray();

            return new Form<SaveAcquisitionProfileDataV1>(form =>
            {
                form.Hidden(item => item.MediaTypeId);
                form.Hidden(item => item.MediaSubtypeId);
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
                form.Number(item => item.DelayMinutes, "profile.delay");
                form.Choices(item => item.FallbackQualities, "profile.fallbackQualities", qualities);
                form.Number(item => item.FallbackAfterHours, "profile.fallbackAfter");
                form.Submit("common.save");
            }, Address(AcquisitionProfileId, NewFor, NewForSubtype), csrf, input, _input is null ? null : FormError);
        }

        // Rules in their order; Require and Reject decide, Prefer and Avoid score, Info only names a match.
        private void DefineRules(List<IUiComponent> content, AcquisitionProfileDetailsV1 profile, string? csrf)
        {
            content.Add(new H2(new Text("profile.rules")));
            content.Add(new Table<ReleaseRuleV1>(_rules)
                .Text(rule => rule.Name, "profile.rule.name")
                .Content("profile.rule.condition", rule => new Span(new Text("profile.rule.field." + rule.Field), new ValueText(" "), new Text("profile.rule.match." + rule.Match),
                    new ValueText(" " + rule.Value)))
                .Content("profile.rule.effect", rule => new Span(new Text("profile.rule.effect." + rule.Effect),
                    new ValueText(rule.Score == 0 ? "" : " " + rule.Score.ToString("+0;-0", CultureInfo.InvariantCulture))))
                .Content("common.actions", rule => new Form<ReleaseRuleParametersV1>(form =>
                {
                    form.Hidden(item => item.Version);
                    form.Submit("profile.rule.remove", rule.Name, valueHidden: true);
                }, Address(profile.Id, handler: "RemoveRule") + "&position=" + rule.Position.ToString(CultureInfo.InvariantCulture), csrf,
                    new ReleaseRuleParametersV1(profile.Id, rule.Position, profile.Version))));

            content.Add(new Form<AddReleaseRuleDataV1>(form =>
            {
                form.Hidden(item => item.Version);
                form.Text(item => item.Name, "profile.rule.name");
                form.Select(item => item.Field, "profile.rule.field", Options<ReleaseRuleField>("profile.rule.field."));
                form.Select(item => item.Match, "profile.rule.match", Options<ReleaseRuleMatch>("profile.rule.match."));
                form.Text(item => item.Value, "profile.rule.value");
                form.Select(item => item.Effect, "profile.rule.effect", Options<ReleaseRuleEffect>("profile.rule.effect."));
                form.Number(item => item.Score, "wanted.search.score");
                form.Submit("request.rule.add");
            }, Address(profile.Id, handler: "Rule"), csrf,
                _ruleInput ?? new AddReleaseRuleDataV1("", ReleaseRuleField.RawTitle, ReleaseRuleMatch.Contains, "", ReleaseRuleEffect.Prefer, 10, profile.Version),
                _ruleInput is null ? null : FormError));
        }

        private SelectOption[] Options<TEnum>(string prefix) where TEnum : struct, Enum =>
            [.. Enum.GetValues<TEnum>().Select(value => new SelectOption(value.ToString(), texts[prefix + value]))];

        public async Task<IActionResult> OnPostRuleAsync([BindRequired] ReleaseRuleField field, [BindRequired] ReleaseRuleMatch match, [BindRequired] ReleaseRuleEffect effect,
            [BindRequired] long version, string? name, string? value, int? score)
        {
            var id = AcquisitionProfileId ?? throw new KeyNotFoundException("No profile was selected.");
            _ruleInput = new AddReleaseRuleDataV1(name ?? "", field, match, value ?? "", effect, score ?? 0, version);
            await addRule.ExecuteAsync<UpdateResult>(new AcquisitionProfileParametersV1(id), _ruleInput, HttpContext.RequestAborted);
            return LocalRedirect(Address(id));
        }

        public async Task<IActionResult> OnPostRemoveRuleAsync([BindRequired] int position, [BindRequired] long version)
        {
            var id = AcquisitionProfileId ?? throw new KeyNotFoundException("No profile was selected.");
            await deleteRule.ExecuteAsync<UpdateResult>(new ReleaseRuleParametersV1(id, position, version), default, HttpContext.RequestAborted);
            return LocalRedirect(Address(id));
        }

        // Sensible starting points per kind.
        private static SaveAcquisitionProfileDataV1 NewProfile(MediaType mediaType, MediaSubtype? subtype) => mediaType is MediaType.Movie or MediaType.Series
            ? new SaveAcquisitionProfileDataV1("", mediaType, false, ["WEB-1080p", "BLURAY-1080p"], true, "WEB-1080p", null, null, [], [], null, subtype)
            : new SaveAcquisitionProfileDataV1("", mediaType, false, ["EPUB"], false, null, null, null, [], [], null, subtype);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string? name, [BindRequired] bool isDefault, string[]? allowedQualities, [BindRequired] bool upgradeAllowed,
            string? upgradeCutoffQuality, int? minimumSizeMb, int? maximumSizeMb, string? mustContain, string? mustNotContain, long? version, int? delayMinutes,
            string[]? fallbackQualities, int? fallbackAfterHours)
        {
            _input = new SaveAcquisitionProfileDataV1(name ?? "", mediaTypeId, isDefault, allowedQualities ?? [], upgradeAllowed, Blank(upgradeCutoffQuality), minimumSizeMb, maximumSizeMb,
                Terms(mustContain), Terms(mustNotContain), version, mediaSubtypeId, delayMinutes ?? 0, fallbackQualities, fallbackAfterHours);
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
            if (_selected is { } selected)
            {
                _rules = await readRules.ExecuteAsync<BaseDataList<ReleaseRuleV1>>(new AcquisitionProfileParametersV1(selected.Id), HttpContext.RequestAborted);
            }
            if (NewFor is { } type && !Kinds.Contains((type, NewForSubtype))) NewFor = null;
        }

        private SelectOption[] YesNo() => [new SelectOption("True", texts["common.yes"]), new SelectOption("False", texts["common.no"])];

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        private static string[] Terms(string? value) => value is null ? [] : [.. value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

        private string Address(long? id = null, MediaType? newFor = null, MediaSubtype? newForSubtype = null, string? handler = null)
        {
            var address = SetupWizard.Address(Request, SetupStepType.Profiles, "/Admin/Acquisition/Profiles");
            if (id is { } profileId) address += "?AcquisitionProfileId=" + profileId.ToString(CultureInfo.InvariantCulture);
            else if (newFor is { } type) address += "?NewFor=" + type + (newForSubtype is { } subtype ? "&NewForSubtype=" + subtype : "");
            return handler is null ? address : address + (address.Contains('?') ? "&" : "?") + "handler=" + handler;
        }
    }
}
