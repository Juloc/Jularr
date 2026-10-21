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

namespace Jularr.Frontend.Pages.Admin.Profile
{
    public sealed class ProfileModel(ReadAcquisitionProfileV1 read, CreateAcquisitionProfileV1 create, UpdateAcquisitionProfileV1 update, DeleteAcquisitionProfileV1 delete,
        ReadCustomFormatsV1 formats, ReadCustomFormatV1 readFormat, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string NewId = "New";
        private AcquisitionProfileDetailsV1? _profile;
        private SaveAcquisitionProfileDataV1? _input;
        private PageResult<CustomFormatDetailsV1>? _formats;

        [BindProperty(SupportsGet = true)] public string Id { get; set; } = "";
        [BindProperty(SupportsGet = true, Name = "type")] public MediaType? NewType { get; set; }
        [BindProperty(SupportsGet = true, Name = "subtype")] public MediaSubtype? NewSubtype { get; set; }
        [BindProperty] public string? Name { get; set; }
        [BindProperty] public MediaType MediaTypeId { get; set; }
        [BindProperty] public MediaSubtype? MediaSubtypeId { get; set; }
        [BindProperty] public bool IsDefault { get; set; }
        [BindProperty] public string[]? AllowedQualities { get; set; }
        [BindProperty] public bool UpgradeAllowed { get; set; }
        [BindProperty] public string? UpgradeCutoffQuality { get; set; }
        [BindProperty] public int? MinimumSizeMb { get; set; }
        [BindProperty] public int? MaximumSizeMb { get; set; }
        [BindProperty] public long? Version { get; set; }
        [BindProperty] public string? QualityOrder { get; set; }

        // The languages of the choice, in order, separated by commas.
        [BindProperty] public string? LanguageOrder { get; set; }

        // The fallback when no file carries an asked language.
        [BindProperty] public LanguageFallbackType LanguageFallback { get; set; } = LanguageFallbackType.Original;
        [BindProperty] public List<GroupRow> QualityGroups { get; set; } = [];
        [BindProperty] public List<ScoreRow> FormatScores { get; set; } = [];
        [BindProperty] public int MinimumFormatScore { get; set; }
        [BindProperty] public int CutoffFormatScore { get; set; }
        [BindProperty] public int MinimumUpgradeFormatScore { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public int FormatPage { get; set; } = 1;

        private long? ProfileId => long.TryParse(Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
        private MediaType Scope => _profile?.MediaTypeId ?? NewType ?? MediaType.Movie;

        protected override void Define(Page page)
        {
            page.Title("admin.profiles.title", _profile?.Name);
            if (_profile is not null) page.Parent("admin.profiles.title", "/Admin/Acquisition/Profiles");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Profiles/Profiles.entry.scss");
            if (SetupWizard.IsActive(Request)) page.Content.Add(SetupWizard.Frame(SetupStepType.Profiles));
            page.Content.Add(AcquisitionSettingsNavigation.Frame(Request, "profiles", Scope));
            page.Content.Add(new A(new Text("common.back")).Class("button").Attribute("href", AcquisitionSettingsNavigation.Address(Request, "profiles", Scope)));
            if (_formats is null || _profile is null && Id != NewId) return;
            var input = _input ?? (_profile is { } profile ? FromProfile(profile) : NewProfile(NewType!.Value, NewSubtype));
            var order = DisplayOrder(input);
            var choices = order.Select(quality => new SelectOption(quality, input.QualityGroups.FirstOrDefault(group => group.Key == quality)?.Name ?? quality)).ToArray();
            var token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var form = new Form<SaveAcquisitionProfileDataV1>(fields =>
            {
                fields.Hidden(item => item.MediaTypeId);
                fields.Hidden(item => item.MediaSubtypeId);
                fields.Hidden(item => item.Version);
                fields.Section("profile.section.general", section =>
                {
                    section.Text(item => item.Name, "profile.name");
                    section.Toggle(item => item.IsDefault, "profile.isDefault");
                });
                fields.Section("profile.section.qualities", section =>
                {
                    section.ChoiceTable("profile.qualities", choices, [section.Column(item => item.AllowedQualities, "profile.allowed"),
                        section.Column(item => item.UpgradeCutoffQuality, "profile.cutoff", single: true, noneKey: "profile.noCutoff")],
                        choice => new Div(Draft("arr.moveUp", "moveUp", choice.Value), Draft("arr.moveDown", "moveDown", choice.Value)));
                    section.Toggle(item => item.UpgradeAllowed, "profile.upgradeAllowed");
                    section.Hidden(item => item.QualityOrder);
                });
                fields.Section("arr.qualityGroups", section =>
                {
                    section.Rows(item => item.QualityGroups, (row, group) =>
                    {
                        row.Hidden(item => item.Id);
                        row.Text(item => item.Name, "profile.name");
                        row.Text(item => item.Qualities, "profile.qualities");
                        row.Content(Draft("common.remove", "removeGroupId", group.Id.ToString(CultureInfo.InvariantCulture)));
                    });
                    section.Content(Draft("collections.add", "addGroup", "true"));
                    section.Content(Draft("arr.refreshGroups", "refreshGroups", "true"));
                });
                fields.Section("arr.formats", section =>
                {
                    section.Number(item => item.MinimumFormatScore, "arr.minimumScore");
                    section.Number(item => item.CutoffFormatScore, "arr.cutoffScore");
                    section.Number(item => item.MinimumUpgradeFormatScore, "arr.upgradeScore");
                    section.Rows(item => item.FormatScores, (row, score) =>
                    {
                        row.Hidden(item => item.FormatId);
                        if (_formats?.Items.FirstOrDefault(format => format.Id == score.FormatId) is { } named)
                        {
                            row.Content(new P(new ValueText(named.Name)));
                            row.Number(item => item.Score, "wanted.search.score");
                            row.Content(Draft("common.remove", "removeFormatId", score.FormatId.ToString(CultureInfo.InvariantCulture)));
                        }
                        else row.Hidden(item => item.Score);
                    });
                    if (_formats is { } pageOfFormats)
                    {
                        section.Content(new Table<CustomFormatDetailsV1>(pageOfFormats.Items.Where(format => !input.FormatScores.Any(score => score.FormatId == format.Id)).ToArray())
                            .Text(format => format.Name, "profile.name")
                            .Content("common.actions", format => Draft("collections.add", "addFormatId", format.Id.ToString(CultureInfo.InvariantCulture))));
                        if (pageOfFormats.Page > 1) section.Content(Draft("common.previous", "formatPage", (pageOfFormats.Page - 1).ToString(CultureInfo.InvariantCulture)));
                        if (pageOfFormats.Items.Count == pageOfFormats.PageSize) section.Content(Draft("common.next", "formatPage", (pageOfFormats.Page + 1).ToString(CultureInfo.InvariantCulture)));
                    }
                });
                fields.Section("profile.section.size", section =>
                {
                    section.Number(item => item.MinimumSizeMb, "profile.minimumSize");
                    section.Number(item => item.MaximumSizeMb, "profile.maximumSize");
                });
                if (_profile is { } imported && (imported.MonitoredTypes.Length > 0 || imported.MonitoredLanguages.Length > 0 || imported.MinimumPages is not null))
                {
                    fields.Section("profile.monitoring", section =>
                    {
                        if (imported.MonitoredTypes.Length > 0) section.Content(new P(new Text("profile.monitoring.types"), new ValueText(": " + string.Join(", ", imported.MonitoredTypes))));
                        if (imported.MonitoredLanguages.Length > 0) section.Content(new P(new Text("profile.monitoring.languages"), new ValueText(": " + string.Join(", ", imported.MonitoredLanguages))));
                        if (imported.MinimumPages is { } pages) section.Content(new P(new Text("profile.monitoring.pages"), new ValueText(": " + pages.ToString(CultureInfo.InvariantCulture))));
                    });
                }
                fields.Section("profile.section.languages", section =>
                {
                    section.Text(item => item.LanguageOrder, "profile.languageOrder");
                    section.Select(item => item.LanguageFallback, "profile.languageFallback",
                        Enum.GetValues<LanguageFallbackType>().Select(fallback => new SelectOption(((byte)fallback).ToString(CultureInfo.InvariantCulture),
                            texts["profile.languageFallback." + fallback])));
                });
                fields.Save();
            }, Address(), token, input, FormError);
            page.Content.Add(form);
            if (_profile is { } saved && !saved.IsDefault)
            {
                page.Content.Add(new Details(new Summary(new Text("profile.deleteConfirm")), new Form<DeleteAcquisitionProfileParametersV1>(fields =>
                {
                    fields.Hidden(item => item.Version);
                    fields.Submit("profile.delete");
                }, Address("Delete"), token, new DeleteAcquisitionProfileParametersV1(saved.Id, saved.Version), _input is null ? FormError : null)));
            }
        }

        private IUiComponent Draft(string key, string command, string value) => new Button(new Text(key)).Attribute("type", "submit").Attribute("formaction", Address("Draft"))
            .Attribute("formnovalidate", "formnovalidate").Attribute("name", command).Attribute("value", value);

        private static SaveAcquisitionProfileDataV1 FromProfile(AcquisitionProfileDetailsV1 profile) => new(profile.Name, profile.MediaTypeId, profile.IsDefault, profile.AllowedQualities,
            profile.UpgradeAllowed, profile.UpgradeCutoffQuality, profile.MinimumSizeMb, profile.MaximumSizeMb, profile.Version, profile.MediaSubtypeId, profile.QualityOrder,
            profile.QualityGroups, profile.FormatScores, profile.MinimumFormatScore, profile.CutoffFormatScore, profile.MinimumUpgradeFormatScore,
            profile.LanguageOrder, profile.LanguageFallback);

        private static SaveAcquisitionProfileDataV1 NewProfile(MediaType type, MediaSubtype? subtype) => new("", type, false, ProfileQualities.StartingQualities(type, subtype),
            ProfileQualities.StartingCutoff(type) is not null, ProfileQualities.StartingCutoff(type), null, null, null, subtype);

        private SaveAcquisitionProfileDataV1 Posted() => new(Name ?? "", MediaTypeId, IsDefault, AllowedQualities ?? [], UpgradeAllowed, UpgradeCutoffQuality, MinimumSizeMb,
            MaximumSizeMb, Version, MediaSubtypeId, Terms(QualityOrder), new(QualityGroups.Select(group => new QualityGroupV1(group.Id, group.Name ?? "", Terms(group.Qualities)))),
            new(FormatScores.Select(score => new CustomFormatScoreV1(score.FormatId, score.Score))), MinimumFormatScore, CutoffFormatScore, MinimumUpgradeFormatScore,
            Terms(LanguageOrder), LanguageFallback);

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            _input = Posted();
            if (ProfileId is { } id) await update.ExecuteAsync<UpdateResult>(new AcquisitionProfileParametersV1(id), _input, HttpContext.RequestAborted);
            else
            {
                var created = await create.ExecuteAsync<CreateAcquisitionProfileResultV1>(new CreateAcquisitionProfileParametersV1(), _input, HttpContext.RequestAborted);
                Id = created.Id.ToString(CultureInfo.InvariantCulture);
            }

            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostDraftAsync(bool addGroup, long? addFormatId, long? removeFormatId, long? removeGroupId, bool refreshGroups, string? moveUp, string? moveDown)
        {
            if (addGroup && QualityGroups.Count < 32) QualityGroups.Add(new GroupRow(Enumerable.Range(1, 33).Select(value => (long)value).First(id => !QualityGroups.Any(group => group.Id == id)), null, null));
            if (removeGroupId is { } removedGroup) QualityGroups.RemoveAll(group => group.Id == removedGroup);
            if (removeFormatId is { } removed) FormatScores.RemoveAll(score => score.FormatId == removed);
            _input = Posted();
            await LoadDataAsync();
            if (addFormatId is { } added && !FormatScores.Any(score => score.FormatId == added))
            {
                var format = await readFormat.ExecuteAsync<CustomFormatDetailsV1>(new CustomFormatParametersV1(added), HttpContext.RequestAborted);
                if (format.MediaTypeId != Scope) throw new KeyNotFoundException("The format does not belong to this media type.");
                FormatScores.Add(new ScoreRow(added, 0));
            }

            var order = DisplayOrder(_input).ToList();
            var moved = moveUp ?? moveDown;
            if (moved is not null && order.IndexOf(moved) is >= 0 and var position)
            {
                var target = Math.Clamp(position + (moveUp is null ? 1 : -1), 0, order.Count - 1);
                (order[position], order[target]) = (order[target], order[position]);
            }

            QualityOrder = string.Join(", ", order);
            _input = Posted();
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostDeleteAsync(long version)
        {
            await LoadDataAsync();
            await delete.ExecuteAsync<UpdateResult>(new DeleteAcquisitionProfileParametersV1(ProfileId ?? throw new KeyNotFoundException(), version), default, HttpContext.RequestAborted);
            return LocalRedirect(AcquisitionSettingsNavigation.Address(Request, "profiles", Scope));
        }

        protected override async Task LoadDataAsync()
        {
            if (ProfileId is { } id) _profile = await read.ExecuteAsync<AcquisitionProfileDetailsV1>(new AcquisitionProfileParametersV1(id), HttpContext.RequestAborted);
            else if (Id != NewId || NewType is not { } type || !ProfilesModel.Kinds.Contains((type, NewSubtype))) throw new KeyNotFoundException("The profile was not found.");
            _formats = await formats.ExecuteAsync<PageResult<CustomFormatDetailsV1>>(new ReadCustomFormatsParametersV1(Scope, FormatPage, 25), HttpContext.RequestAborted);
        }

        private string Address(string? handler = null)
        {
            var address = ProfilesModel.Home(Request) + "/" + (ProfileId?.ToString(CultureInfo.InvariantCulture) ?? NewId);
            if (ProfileId is null && NewType is { } type) address += "?type=" + type + (NewSubtype is { } subtype ? "&subtype=" + subtype : "");
            return handler is null ? address : address + (address.Contains('?') ? "&" : "?") + "handler=" + handler;
        }

        private static string[] Terms(string? value) => value is null ? [] : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        private static string[] DisplayOrder(SaveAcquisitionProfileDataV1 input)
        {
            var groups = input.QualityGroups.Where(group => !string.IsNullOrWhiteSpace(group.Name) && group.Qualities.Length > 0).ToArray();
            var grouped = groups.SelectMany(group => group.Qualities).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var available = ProfileQualities.For(input.MediaTypeId, input.MediaSubtypeId).Where(quality => !grouped.Contains(quality)).Concat(groups.Select(group => group.Key)).ToArray();
            return input.QualityOrder.Select(quality => groups.FirstOrDefault(group => group.Qualities.Contains(quality, StringComparer.OrdinalIgnoreCase))?.Key ?? quality)
                .Where(available.Contains).Concat(available).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public sealed record GroupRow(long Id, string? Name, string? Qualities);
        public sealed record ScoreRow(long FormatId, int Score);
    }
}
