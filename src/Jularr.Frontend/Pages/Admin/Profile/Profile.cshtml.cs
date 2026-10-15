using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Profiles;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Profile
{
    // One quality profile: its qualities as a ladder, sizes and terms, then its release rules.
    public sealed class ProfileModel(ReadAcquisitionProfilesV1 profiles, CreateAcquisitionProfileV1 create, UpdateAcquisitionProfileV1 update, DeleteAcquisitionProfileV1 delete,
        ReadAcquisitionProfileRulesV1 readRules, AddAcquisitionProfileRuleV1 addRule, UpdateAcquisitionProfileRuleV1 updateRule, DeleteAcquisitionProfileRuleV1 deleteRule,
        IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string NewId = "New";
        private AcquisitionProfileDetailsV1? _profile;
        private SaveAcquisitionProfileDataV1? _input;
        private SaveReleaseRuleDataV1? _ruleInput;
        private IReadOnlyList<ReleaseRuleV1> _rules = [];

        // A profile id, or New with its media type.
        [BindProperty(SupportsGet = true)]
        public string Id { get; set; } = "";

        [BindProperty(SupportsGet = true, Name = "type")]
        public MediaType? NewType { get; set; }

        [BindProperty(SupportsGet = true, Name = "subtype")]
        public MediaSubtype? NewSubtype { get; set; }

        // The rule being edited.
        [BindProperty(SupportsGet = true, Name = "rule")]
        public int? RuleId { get; set; }

        [BindProperty(SupportsGet = true, Name = "delete")]
        public bool ConfirmDelete { get; set; }

        private long? ProfileId => long.TryParse(Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

        protected override void Define(Page page)
        {
            page.Title("admin.profiles.title", _profile?.Name);
            page.Navigation("nav.admin");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            if (SetupWizard.IsActive(Request)) page.Content.Add(SetupWizard.Frame(SetupStepType.Profiles, csrf));
            page.Content.Add(new Div(new A(new Text("common.back")).Class("button").Attribute("href", ProfilesModel.Home(Request))).Class("page-actions"));
            page.Content.Add(ProfileForm(csrf));
            if (FormError is not null && _input is null && _ruleInput is null) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
            if (_profile is not { } profile) return;

            page.Content.Add(new UiSection("profile.rules", [RulesTable(profile, csrf), RuleForm(profile, csrf)]));

            // The default of a media type stays; deleting asks once more.
            if (!profile.IsDefault)
            {
                page.Content.Add(ConfirmDelete
                    ? new Div(new P(new Text("profile.deleteConfirm")), new Form<DeleteAcquisitionProfileParametersV1>(form =>
                    {
                        form.Hidden(item => item.Version);
                        form.Submit("profile.delete");
                    }, Address(handler: "Delete"), csrf, new DeleteAcquisitionProfileParametersV1(profile.Id, profile.Version)),
                        new A(new Text("common.back")).Class("button").Attribute("href", Address())).Class("page-actions")
                    : new A(new Text("profile.delete")).Class("button").Attribute("href", Address() + "?delete=true"));
            }
        }

        private Form<SaveAcquisitionProfileDataV1> ProfileForm(string? csrf)
        {
            var input = _input ?? (_profile is { } profile
                ? new SaveAcquisitionProfileDataV1(profile.Name, profile.MediaTypeId, profile.IsDefault, profile.AllowedQualities, profile.UpgradeAllowed, profile.UpgradeCutoffQuality,
                    profile.MinimumSizeMb, profile.MaximumSizeMb, profile.MustContain, profile.MustNotContain, profile.Version, profile.MediaSubtypeId, profile.DelayMinutes,
                    profile.FallbackQualities, profile.FallbackAfterHours)
                : NewProfile(NewType!.Value, NewSubtype));
            var qualities = ProfileQualities.For(input.MediaTypeId, input.MediaSubtypeId).Select(quality => new SelectOption(quality, quality)).ToArray();
            var yesNo = new[] { new SelectOption("True", texts["common.yes"]), new SelectOption("False", texts["common.no"]) };

            return new Form<SaveAcquisitionProfileDataV1>(form =>
            {
                form.Hidden(item => item.MediaTypeId);
                form.Hidden(item => item.MediaSubtypeId);
                form.Hidden(item => item.Version);
                form.Section("profile.section.general", section =>
                {
                    section.Text(item => item.Name, "profile.name");
                    section.Select(item => item.IsDefault, "profile.isDefault", yesNo);
                    section.Number(item => item.DelayMinutes, "profile.delay");
                });

                // Best first: what may be taken, where upgrades stop and what is accepted after waiting.
                form.Section("profile.section.qualities", section =>
                {
                    section.ChoiceTable("profile.qualities", qualities, section.Column(item => item.AllowedQualities, "profile.allowed"),
                        section.Column(item => item.UpgradeCutoffQuality, "profile.cutoff", single: true, noneKey: "profile.noCutoff"),
                        section.Column(item => item.FallbackQualities, "profile.fallbackQualities"));
                    section.Select(item => item.UpgradeAllowed, "profile.upgradeAllowed", yesNo);
                    section.Number(item => item.FallbackAfterHours, "profile.fallbackAfter");
                });
                form.Section("profile.section.size", section =>
                {
                    section.Number(item => item.MinimumSizeMb, "profile.minimumSize");
                    section.Number(item => item.MaximumSizeMb, "profile.maximumSize");
                    section.Text(item => item.MustContain, "profile.mustContain");
                    section.Text(item => item.MustNotContain, "profile.mustNotContain");
                });
                form.Submit("common.save");
            }, Address(), csrf, input, _input is null ? null : FormError);
        }

        // Require and Reject decide, Prefer and Avoid score, Info only names a match.
        private IUiComponent RulesTable(AcquisitionProfileDetailsV1 profile, string? csrf) => _rules.Count == 0 ? new P(new Text("profile.rule.none"))
            : new Table<ReleaseRuleV1>(_rules)
                .Text(rule => rule.Name, "profile.rule.name")
                .Content("profile.rule.condition", rule => new Span(new Text("profile.rule.field." + rule.Field), new ValueText(" "), new Text("profile.rule.match." + rule.Match),
                    new ValueText(" " + rule.Value)))
                .Content("profile.rule.effect", rule => new Span(new Text("profile.rule.effect." + rule.Effect),
                    new ValueText(rule.Score == 0 ? "" : " " + rule.Score.ToString("+0;-0", CultureInfo.InvariantCulture))))
                .Content("common.actions", rule => new Div(
                    new A(new Text("common.edit")).Class("button").Attribute("href", Address() + "?rule=" + rule.Id.ToString(CultureInfo.InvariantCulture)),
                    new Form<ReleaseRuleParametersV1>(form =>
                    {
                        form.Hidden(item => item.Version);
                        form.Submit("profile.rule.remove", rule.Name, valueHidden: true);
                    }, Address(handler: "RemoveRule") + "&ruleId=" + rule.Id.ToString(CultureInfo.InvariantCulture), csrf,
                        new ReleaseRuleParametersV1(profile.Id, rule.Id, profile.Version))).Class("page-actions"));

        // A new rule, or the chosen one in place.
        private IUiComponent RuleForm(AcquisitionProfileDetailsV1 profile, string? csrf)
        {
            var edited = RuleId is { } ruleId ? _rules.FirstOrDefault(rule => rule.Id == ruleId) : null;
            var input = _ruleInput ?? (edited is { } rule
                ? new SaveReleaseRuleDataV1(rule.Name, rule.Field, rule.Match, rule.Value, rule.Effect, Math.Abs(rule.Score), profile.Version)
                : new SaveReleaseRuleDataV1("", ReleaseRuleField.RawTitle, ReleaseRuleMatch.Contains, "", ReleaseRuleEffect.Prefer, 10, profile.Version));
            return new Form<SaveReleaseRuleDataV1>(form =>
            {
                form.Hidden(item => item.Version);
                form.Section(edited is null ? "request.rule.add" : "profile.rule.edit", section =>
                {
                    section.Text(item => item.Name, "profile.rule.name");
                    section.Select(item => item.Field, "profile.rule.field", Options<ReleaseRuleField>("profile.rule.field."));
                    section.Select(item => item.Match, "profile.rule.match", Options<ReleaseRuleMatch>("profile.rule.match."));
                    section.Text(item => item.Value, "profile.rule.value");
                    section.Select(item => item.Effect, "profile.rule.effect", Options<ReleaseRuleEffect>("profile.rule.effect."));
                    section.Number(item => item.Score, "wanted.search.score");
                });
                form.Submit(edited is null ? "collections.add" : "common.save");
            }, Address(handler: "Rule") + (edited is null ? "" : "&rule=" + edited.Id.ToString(CultureInfo.InvariantCulture)), csrf, input, _ruleInput is null ? null : FormError);
        }

        // Catalog values travel as their numbers, like the form shows the stored one.
        private SelectOption[] Options<TEnum>(string prefix) where TEnum : struct, Enum =>
            [.. Enum.GetValues<TEnum>().Select(value => new SelectOption(Convert.ToByte(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), texts[prefix + value]))];

        private static SaveAcquisitionProfileDataV1 NewProfile(MediaType mediaType, MediaSubtype? subtype) => new("", mediaType, false,
            ProfileQualities.StartingQualities(mediaType, subtype), ProfileQualities.StartingCutoff(mediaType) is not null, ProfileQualities.StartingCutoff(mediaType), null, null, [], [], null, subtype);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] MediaType mediaTypeId, MediaSubtype? mediaSubtypeId, string? name, [BindRequired] bool isDefault, string[]? allowedQualities,
            [BindRequired] bool upgradeAllowed, string? upgradeCutoffQuality, int? minimumSizeMb, int? maximumSizeMb, string? mustContain, string? mustNotContain, long? version, int? delayMinutes,
            string[]? fallbackQualities, int? fallbackAfterHours)
        {
            _input = new SaveAcquisitionProfileDataV1(name ?? "", mediaTypeId, isDefault, allowedQualities ?? [], upgradeAllowed, upgradeCutoffQuality, minimumSizeMb, maximumSizeMb,
                Terms(mustContain), Terms(mustNotContain), version, mediaSubtypeId, delayMinutes ?? 0, fallbackQualities, fallbackAfterHours);
            if (ProfileId is { } existing)
            {
                await update.ExecuteAsync<UpdateResult>(new AcquisitionProfileParametersV1(existing), _input, HttpContext.RequestAborted);
                return LocalRedirect(Address());
            }

            var created = await create.ExecuteAsync<CreateAcquisitionProfileResultV1>(new CreateAcquisitionProfileParametersV1(), _input, HttpContext.RequestAborted);
            Id = created.Id.ToString(CultureInfo.InvariantCulture);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostRuleAsync([BindRequired] ReleaseRuleField field, [BindRequired] ReleaseRuleMatch match, [BindRequired] ReleaseRuleEffect effect,
            [BindRequired] long version, string? name, string? value, int? score)
        {
            var id = SelectedId();
            _ruleInput = new SaveReleaseRuleDataV1(name ?? "", field, match, value ?? "", effect, score ?? 0, version);
            if (RuleId is { } ruleId)
            {
                await updateRule.ExecuteAsync<UpdateResult>(new ReleaseRuleKeyParametersV1(id, ruleId), _ruleInput, HttpContext.RequestAborted);
            }
            else
            {
                await addRule.ExecuteAsync<UpdateResult>(new AcquisitionProfileParametersV1(id), _ruleInput, HttpContext.RequestAborted);
            }

            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostRemoveRuleAsync([BindRequired] int ruleId, [BindRequired] long version)
        {
            await deleteRule.ExecuteAsync<UpdateResult>(new ReleaseRuleParametersV1(SelectedId(), ruleId, version), default, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostDeleteAsync([BindRequired] long version)
        {
            await delete.ExecuteAsync<UpdateResult>(new DeleteAcquisitionProfileParametersV1(SelectedId(), version), default, HttpContext.RequestAborted);
            return LocalRedirect(ProfilesModel.Home(Request));
        }

        protected override async Task LoadDataAsync()
        {
            if (ProfileId is { } id)
            {
                var all = await profiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(), HttpContext.RequestAborted);
                _profile = all.FirstOrDefault(profile => profile.Id == id) ?? throw new KeyNotFoundException("The profile was not found.");
                _rules = await readRules.ExecuteAsync<BaseDataList<ReleaseRuleV1>>(new AcquisitionProfileParametersV1(id), HttpContext.RequestAborted);
            }
            else if (Id != NewId || NewType is not { } type || !ProfilesModel.Kinds.Contains((type, NewSubtype)))
            {
                throw new KeyNotFoundException("The profile was not found.");
            }
        }

        private long SelectedId() => ProfileId ?? throw new KeyNotFoundException("No profile was selected.");

        private static string[] Terms(string? value) => value is null ? [] : [.. value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

        private string Address(string? handler = null)
        {
            var address = ProfilesModel.Home(Request) + "/" + (ProfileId?.ToString(CultureInfo.InvariantCulture) ?? NewId);
            if (ProfileId is null && NewType is { } type) address += "?type=" + type + (NewSubtype is { } subtype ? "&subtype=" + subtype : "");
            return handler is null ? address : address + (address.Contains('?') ? "&" : "?") + "handler=" + handler;
        }
    }
}
