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
        ReadCustomFormatsV1 formats, ReadCustomFormatV1 readFormat, FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string NewId = "New";
        private AcquisitionProfileDetailsV1? _profile;
        private SaveAcquisitionProfileDataV1? _input;
        private List<CustomFormatDetailsV1> _formats = [];

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

        private long? ProfileId => long.TryParse(Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
        private MediaType Scope => _profile?.MediaTypeId ?? NewType ?? MediaType.Movie;

        protected override void Define(Page page)
        {
            page.Title(_profile is null ? "profile.new" : "admin.profiles.title", _profile?.Name);
            page.Parent("admin.profiles.title", BackAddress());
            page.Navigation("nav.admin");
            page.Asset("Framework/Components/Modal.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            if (SetupWizard.IsActive(Request)) page.Content.Add(SetupWizard.Frame(HttpContext, SetupStepType.Profiles));
            if (_profile is null && Id != NewId) return;
            var input = WithMatrix(_input ?? (_profile is { } profile ? FromProfile(profile) : NewProfile(NewType!.Value, NewSubtype)));
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
                fields.Section("arr.scores", section =>
                {
                    section.Number(item => item.MinimumFormatScore, "arr.minimumScore");
                    section.Number(item => item.CutoffFormatScore, "arr.cutoffScore");
                    section.Number(item => item.MinimumUpgradeFormatScore, "arr.upgradeScore");

                    // The matrix: every custom format of the media type with this profile's score (0 leaves it out).
                    section.Rows(item => item.FormatScores, (row, score) =>
                    {
                        row.Hidden(item => item.FormatId);
                        if (_formats.FirstOrDefault(format => format.Id == score.FormatId) is { } named)
                        {
                            row.Content(new P(new ValueText(named.Name)));
                            row.Number(item => item.Score, "wanted.search.score");
                        }
                        else row.Hidden(item => item.Score);
                    });
                });
                fields.Section("profile.section.size", section =>
                {
                    section.Number(item => item.MinimumSizeMb, "profile.minimumSize", affix: FieldAffix.Unit("unit.megabytes"));
                    section.Number(item => item.MaximumSizeMb, "profile.maximumSize", affix: FieldAffix.Unit("unit.megabytes"));
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

            // Back to the list, and the deletion of an unused profile behind a dialog.
            var actions = new List<IUiComponent> { new A(Glyph("chevron-left", 16), new Span(new Text("common.back")).Class("action-dock__text")).Class("button").Attribute("href", BackAddress()) };
            if (_profile is { } saved && !saved.IsDefault)
            {
                actions.Add(Modal.Opener(new Button(Glyph("trash", 16), new Span(new Text("profile.delete")).Class("action-dock__text")).Warn(), "profile-delete"));
                page.Content.Add(new Modal(assets, "profile-delete", "profile.deleteConfirm", [new Form<DeleteAcquisitionProfileParametersV1>(fields =>
                {
                    fields.Hidden(item => item.Version);
                    fields.Cancel();
                    fields.Submit("profile.delete");
                }, Address("Delete"), token, new DeleteAcquisitionProfileParametersV1(saved.Id, saved.Version), _input is null ? FormError : null)]));
            }

            page.Content.Add(new ActionDock(assets, "arr.actions", actions));
        }

        // The list this profile belongs to.
        private string BackAddress() => DownloadPages.PaneAddress(DownloadPages.Profiles, Scope, ProfilesModel.Home(Request));

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        // The profile's scores next to every format of the media type, so each can be scored in place.
        private SaveAcquisitionProfileDataV1 WithMatrix(SaveAcquisitionProfileDataV1 input)
        {
            var rows = _formats.Select(format => new CustomFormatScoreV1(format.Id, input.FormatScores.FirstOrDefault(score => score.FormatId == format.Id)?.Score ?? 0)).ToList();
            rows.AddRange(input.FormatScores.Where(score => _formats.All(format => format.Id != score.FormatId)));
            return new(input.Name, input.MediaTypeId, input.IsDefault, input.AllowedQualities, input.UpgradeAllowed, input.UpgradeCutoffQuality, input.MinimumSizeMb, input.MaximumSizeMb, input.Version,
                input.MediaSubtypeId, input.QualityOrder, input.QualityGroups, new(rows), input.MinimumFormatScore, input.CutoffFormatScore, input.MinimumUpgradeFormatScore);
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
            new(FormatScores.Where(score => score.Score != 0).Select(score => new CustomFormatScoreV1(score.FormatId, score.Score))), MinimumFormatScore, CutoffFormatScore, MinimumUpgradeFormatScore,
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

        public async Task<IActionResult> OnPostDraftAsync(bool addGroup, long? removeGroupId, bool refreshGroups, string? moveUp, string? moveDown)
        {
            if (addGroup && QualityGroups.Count < 32) QualityGroups.Add(new GroupRow(Enumerable.Range(1, 33).Select(value => (long)value).First(id => !QualityGroups.Any(group => group.Id == id)), null, null));
            if (removeGroupId is { } removedGroup) QualityGroups.RemoveAll(group => group.Id == removedGroup);
            _input = Posted();
            await LoadDataAsync();

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
            return LocalRedirect(BackAddress());
        }

        protected override async Task LoadDataAsync()
        {
            if (ProfileId is { } id) _profile = await read.ExecuteAsync<AcquisitionProfileDetailsV1>(new AcquisitionProfileParametersV1(id), HttpContext.RequestAborted);
            else if (Id != NewId || NewType is not { } type || !ProfilesModel.Kinds.Contains((type, NewSubtype))) throw new KeyNotFoundException("The profile was not found.");

            // Every format of the media type, page by page.
            _formats = [];
            for (var formatPage = 1; ; formatPage++)
            {
                var read = await formats.ExecuteAsync<PageResult<CustomFormatDetailsV1>>(new ReadCustomFormatsParametersV1(Scope, formatPage, 100), HttpContext.RequestAborted);
                _formats.AddRange(read.Items);
                if (read.Items.Count < read.PageSize) break;
            }
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
