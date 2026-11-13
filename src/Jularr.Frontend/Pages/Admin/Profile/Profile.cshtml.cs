using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Service.Admin.Acquisition.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Profiles;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Profile
{
    public sealed class ProfileModel(ReadAcquisitionProfileV1 read, CreateAcquisitionProfileV1 create, UpdateAcquisitionProfileV1 update, DeleteAcquisitionProfileV1 delete,
        ReadCustomFormatsV1 formats, ReadReleaseGroupFiguresV1 groupFigures, FrontendAssets assets, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string NewId = "New";
        private AcquisitionProfileDetailsV1? _profile;
        private SaveAcquisitionProfileDataV1? _input;
        private List<CustomFormatDetailsV1> _formats = [];
        private ReleaseGroupFiguresV1? _groupFigures;
        private bool _advancedOpen;

        [BindProperty(SupportsGet = true)] public string Id { get; set; } = "";

        // The media type a new profile starts with; it decides the profile's list of qualities.
        [BindProperty(SupportsGet = true, Name = "kind")] public string? NewKindText { get; set; }
        [BindProperty] public string? Name { get; set; }
        [BindProperty] public string[]? MediaKinds { get; set; }
        [BindProperty] public string[]? DefaultFor { get; set; }
        [BindProperty] public string[]? AllowedQualities { get; set; }
        [BindProperty] public string? UpgradeCutoffQuality { get; set; }
        [BindProperty] public int? MinimumSizeMb { get; set; }
        [BindProperty] public int? MaximumSizeMb { get; set; }
        [BindProperty] public long? Version { get; set; }
        [BindProperty] public string? QualityOrder { get; set; }

        // The languages of the choice, in order, separated by commas.
        [BindProperty] public string? LanguageOrder { get; set; }

        // The fallback when no file carries an asked language.
        [BindProperty] public LanguageFallbackType LanguageFallback { get; set; } = LanguageFallbackType.Any;
        [BindProperty] public List<GroupRow> QualityGroups { get; set; } = [];
        [BindProperty] public List<ScoreRow> FormatScores { get; set; } = [];
        [BindProperty] public int MinimumFormatScore { get; set; }
        [BindProperty] public int CutoffFormatScore { get; set; }
        [BindProperty] public int MinimumUpgradeFormatScore { get; set; } = 1;
        [BindProperty] public int GoodGroupScore { get; set; } = 50;
        [BindProperty] public int BadGroupScore { get; set; } = -50;

        // The simple switches. The groups switch names the points of a good and a bad group: none, the default ones, or the expert's own ("custom", shown only then);
        // it wins over the expert fields only when it was changed.
        [BindProperty] public bool BlockBadSources { get; set; } = true;
        [BindProperty] public CodecPreference CodecPreference { get; set; }
        [BindProperty] public SizePreference SizePreference { get; set; }
        [BindProperty] public string? GroupPreference { get; set; }
        [BindProperty] public string? GroupPreferenceWas { get; set; }

        private const string GroupsIgnored = "ignore";
        private const string GroupsRespected = "respect";
        private const string GroupsCustom = "custom";

        // The points of a good and a bad group a new profile starts with.
        private const int DefaultGoodGroupScore = 50;
        private const int DefaultBadGroupScore = -50;

        private static string GroupChoice(int good, int bad) =>
            (good, bad) == (0, 0) ? GroupsIgnored : (good, bad) == (DefaultGoodGroupScore, DefaultBadGroupScore) ? GroupsRespected : GroupsCustom;

        private long? ProfileId => long.TryParse(Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
        private AcquisitionMediaKind? NewKind => Enum.TryParse<AcquisitionMediaKind>(NewKindText, true, out var kind) && Enum.IsDefined(kind) && !int.TryParse(NewKindText, out _) ? kind : null;

        // The quality list of the profile: its media types share one.
        private QualityFamily Family => _profile?.QualityFamily ?? AcquisitionMediaKinds.Family(NewKind ?? AcquisitionMediaKind.Movie);

        protected override void Define(Page page)
        {
            page.Title(_profile is null ? "profile.new" : "admin.profiles.title", _profile?.Name);
            page.Parent("admin.profiles.title", BackAddress());
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Profile/Profile.entry.scss");
            page.Asset("Framework/Components/Modal.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            if (_profile is null && Id != NewId) return;
            var input = WithMatrix(_input ?? (_profile is { } profile ? FromProfile(profile) : NewProfile(NewKind!.Value)));
            var kinds = Enum.GetValues<AcquisitionMediaKind>().Where(kind => AcquisitionMediaKinds.Family(kind) == Family)
                .Select(kind => new SelectOption(kind.ToString(), texts["library.scope." + ProfilesModel.KindSlug(kind)])).ToArray();
            var order = DisplayOrder(input);
            var token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            var form = new Form<SaveAcquisitionProfileDataV1>(fields =>
            {
                fields.Hidden(item => item.Version);
                fields.Section("profile.section.general", section =>
                {
                    section.Text(item => item.Name, "profile.name");

                    // The media types of one quality list: those it applies to, and those whose default it is (one default per type; another profile takes it over).
                    section.Content(KindSwitches(kinds, input.MediaKinds, input.DefaultFor));
                });
                fields.Section("profile.section.qualities", section =>
                {
                    // The allowed qualities as a short list, best first: arrows move one, a cross takes it out, a choice below adds one; upgrades stop at one of them or never start.
                    var allowed = order.Where(quality => input.AllowedQualities.Contains(quality, StringComparer.OrdinalIgnoreCase)).ToArray();
                    section.Content(new P(new Text("profile.qualities.info")).Class("ui-field__help profile-wide"));
                    var rows = allowed.Select((quality, index) => (IUiComponent)new Div(
                        new Input().Attribute("type", "hidden").Attribute("name", "AllowedQualities").Attribute("value", quality),
                        new Span(new ValueText(QualityLabel(input, quality))).Class("profile-quality__name"),
                        input.UpgradeAllowed && string.Equals(quality, input.UpgradeCutoffQuality, StringComparison.OrdinalIgnoreCase) ? new Tag(new Text("profile.cutoffTag")).Accent() : new Span(),
                        new Div(Move("arr.moveUp", "moveUp", quality, index == 0), Move("arr.moveDown", "moveDown", quality, index == allowed.Length - 1),
                            Remove(quality, allowed.Length == 1)).Class("profile-move")).Class("profile-quality")).ToList();
                    section.Content(new Div([.. rows]).Class("profile-qualities"));
                    var missing = order.Where(quality => !allowed.Contains(quality, StringComparer.OrdinalIgnoreCase)).ToArray();
                    if (missing.Length > 0)
                    {
                        var options = missing.Select(quality => (IUiComponent)new OptionElement(new ValueText(QualityLabel(input, quality))).Attribute("value", quality)).ToList();
                        section.Content(new Div(new Label(new Text("profile.addQuality")).Class("visually-hidden").Attribute("for", "profile-add-quality"),
                            new SelectElement([.. options]).Attribute("name", "addQuality").Attribute("id", "profile-add-quality"),
                            Draft("profile.addQuality", "addQualityNow", "true")).Class("profile-add-quality"));
                    }

                    section.Select(item => item.UpgradeCutoffQuality, "profile.cutoff",
                        new[] { new SelectOption("", texts["profile.noCutoff"]) }.Concat(allowed.Select(quality => new SelectOption(quality, QualityLabel(input, quality)))));
                    section.Hidden(item => item.QualityOrder);
                });
                fields.Section("profile.section.prefer", section =>
                {
                    // What counts besides the quality, in plain words; the languages come from the profile languages, the details are the expert's.
                    section.Content(new P(new Text("profile.prefer.info")).Class("ui-field__help profile-wide"));
                    if (Family == QualityFamily.Video)
                    {
                        section.Select(item => item.BlockBadSources, "profile.prefer.badSources",
                            [new SelectOption("False", texts["profile.prefer.allow"]), new SelectOption("True", texts["profile.prefer.never"])], new FieldHelp(Info: "profile.prefer.badSources.info"));
                    }

                    section.Content(GroupSwitch(input));
                    if (Family == QualityFamily.Video)
                    {
                        section.Select(item => item.CodecPreference, "profile.prefer.codec",
                            [new SelectOption(Number((byte)CodecPreference.AvoidHevc), texts["profile.prefer.avoid"]), new SelectOption(Number((byte)CodecPreference.Any), texts["profile.prefer.any"]),
                                new SelectOption(Number((byte)CodecPreference.PreferHevc), texts["profile.prefer.like"])], new FieldHelp(Info: "profile.prefer.codec.info"));
                        section.Select(item => item.SizePreference, "profile.prefer.size",
                            [new SelectOption(Number((byte)SizePreference.Smaller), texts["profile.prefer.smaller"]), new SelectOption(Number((byte)SizePreference.Any), texts["profile.prefer.any"]),
                                new SelectOption(Number((byte)SizePreference.Larger), texts["profile.prefer.larger"])], new FieldHelp(Info: "profile.prefer.size.info"));
                    }
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
                // Everything Sonarr knows stays behind "For experts": size limits, the points of the groups, the scores, quality groups and the score of every format.
                // It opens while one of them is being edited or is wrong.
                var expertFields = new[] { "QualityGroups", "FormatScores", "MinimumSizeMb", "MaximumSizeMb", "GoodGroupScore", "BadGroupScore", "MinimumFormatScore", "CutoffFormatScore",
                    "MinimumUpgradeFormatScore" };
                var advancedOpen = _advancedOpen || FormError?.Field is { } field && expertFields.Any(name => field.StartsWith(name, StringComparison.Ordinal));
                fields.Wrap(parts =>
                {
                    var advanced = new Details(new Summary(new Span(Glyph("sliders", 18)).Class("profile-advanced__icon"),
                            new Span(new Span(new Text("profile.advanced")).Class("profile-advanced__title"), new Span(new Text("profile.advanced.info")).Class("profile-advanced__info")),
                            new Span(Glyph("chevron-down", 18)).Class("profile-advanced__chevron")),
                        new Div([.. parts]).Class("profile-advanced__body")).Class("profile-advanced");
                    return advancedOpen ? advanced.Attribute("open", "open") : advanced;
                }, advanced =>
                {
                    advanced.Section("profile.section.size", section =>
                    {
                        section.Content(ExpertInfo("profile.expert.size"));
                        section.Number(item => item.MinimumSizeMb, "profile.minimumSize", affix: FieldAffix.Unit("unit.megabytes"));
                        section.Number(item => item.MaximumSizeMb, "profile.maximumSize", affix: FieldAffix.Unit("unit.megabytes"));
                    });
                    advanced.Section("arr.releaseGroups", section =>
                    {
                        section.Content(ExpertInfo("profile.expert.groups"));
                        section.Number(item => item.GoodGroupScore, "profile.goodGroupScore", new FieldHelp(Info: "profile.goodGroupScore.info"));
                        section.Number(item => item.BadGroupScore, "profile.badGroupScore", new FieldHelp(Info: "profile.badGroupScore.info"));
                        if (_groupFigures is { } figures)
                        {
                            var label = string.Format(CultureInfo.CurrentCulture, texts["profile.rateGroups"], figures.GroupCount.ToString("N0", CultureInfo.CurrentCulture),
                                figures.BlockedCount.ToString("N0", CultureInfo.CurrentCulture));
                            section.Content(new Div(new A(new ValueText(label)).Class("button").Attribute("href", ProfilesModel.Home(Request) + "?tab=" + DownloadPages.Groups))
                                .Class("ui-field profile-groups"));
                        }
                    });
                    advanced.Section("profile.section.upgrades", section =>
                    {
                        section.Content(ExpertInfo("profile.expert.upgrades"));
                        section.Number(item => item.MinimumFormatScore, "arr.minimumScore", new FieldHelp(Info: "profile.minimumScore.info"));
                        section.Number(item => item.CutoffFormatScore, "arr.cutoffScore", new FieldHelp(Info: "profile.cutoffScore.info"));
                        section.Number(item => item.MinimumUpgradeFormatScore, "arr.upgradeScore", new FieldHelp(Info: "profile.upgradeScore.info"));
                    });
                    // The matrix: every custom format with this profile's score (0 leaves it out).
                    if (input.FormatScores.Count > 0) advanced.Section("arr.scores", section =>
                    {
                        section.Content(ExpertInfo("profile.expert.scores"));
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

                    advanced.Section("arr.qualityGroups", section =>
                    {
                        section.Content(ExpertInfo("profile.expert.qualityGroups"));
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

                });
                fields.Save();
            }, Address(), token, input, FormError);
            page.Content.Add(form);

            // Back to the list, and the deletion of a profile that is no default behind a dialog: titles and open requests keep it, the request rules that offer it lose it.
            var actions = new List<IUiComponent> { new A(Glyph("chevron-left", 16), new Span(new Text("common.back")).Class("action-dock__text")).Class("button").Attribute("href", BackAddress()) };
            if (_profile is { } saved && saved.DefaultFor.Length == 0)
            {
                actions.Add(Modal.Opener(new Button(Glyph("trash", 16), new Span(new Text("profile.delete")).Class("action-dock__text")).Warn(), "profile-delete"));
                var body = new List<IUiComponent>();
                if (saved.WantedCount > 0) body.Add(new P(new Text("profile.delete.hasTitles")).Attribute("role", "alert"));
                else if (saved.OpenRequestCount > 0) body.Add(new P(new Text("profile.delete.hasRequests")).Attribute("role", "alert"));
                else if (saved.RuleCount > 0)
                {
                    body.Add(new P(new ValueText(string.Format(CultureInfo.CurrentCulture, texts[saved.RuleCount == 1 ? "profile.delete.rule" : "profile.delete.rules"],
                        saved.RuleCount.ToString("N0", CultureInfo.CurrentCulture)))));
                }

                var blocked = saved.WantedCount > 0 || saved.OpenRequestCount > 0;
                body.Add(new Form<DeleteAcquisitionProfileParametersV1>(fields =>
                {
                    fields.Hidden(item => item.Version);
                    fields.Cancel();
                    if (!blocked) fields.Submit("profile.delete");
                }, Address("Delete"), token, new DeleteAcquisitionProfileParametersV1(saved.Id, saved.Version), _input is null ? FormError : null));
                page.Content.Add(new Modal(assets, "profile-delete", "profile.deleteConfirm", [.. body]));
            }

            page.Content.Add(new ActionDock(assets, "arr.actions", actions));
        }

        // The list of every profile.
        private string BackAddress() => ProfilesModel.Home(Request);

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        // The profile's scores next to every format, so each can be scored in place.
        private SaveAcquisitionProfileDataV1 WithMatrix(SaveAcquisitionProfileDataV1 input)
        {
            var rows = _formats.Select(format => new CustomFormatScoreV1(format.Id, input.FormatScores.FirstOrDefault(score => score.FormatId == format.Id)?.Score ?? 0)).ToList();
            rows.AddRange(input.FormatScores.Where(score => _formats.All(format => format.Id != score.FormatId)));
            return new(input.Name, input.MediaKinds, input.DefaultFor, input.AllowedQualities, input.UpgradeAllowed, input.UpgradeAllowed ? input.UpgradeCutoffQuality : null,
                input.MinimumSizeMb, input.MaximumSizeMb,
                input.Version, input.QualityOrder, input.QualityGroups, new(rows), input.MinimumFormatScore, input.CutoffFormatScore, input.MinimumUpgradeFormatScore, input.LanguageOrder, input.LanguageFallback,
                input.GoodGroupScore, input.BadGroupScore, input.BlockBadSources, input.CodecPreference, input.SizePreference);
        }

        private static string Number(byte value) => value.ToString(CultureInfo.InvariantCulture);

        // The name of a quality or of a quality group.
        private static string QualityLabel(SaveAcquisitionProfileDataV1 input, string quality) => input.QualityGroups.FirstOrDefault(group => group.Key == quality)?.Name ?? quality;

        // A cross that takes a quality out of the list; the last one stays.
        private IUiComponent Remove(string quality, bool last)
        {
            var button = new Button(Glyph("close", 16)).IconOnly(texts["common.remove"]).Submit();
            if (last) button.Disabled();
            return button.Attribute("title", texts["common.remove"]).Attribute("formaction", Address("Draft")).Attribute("formnovalidate", "formnovalidate")
                .Attribute("name", "removeQuality").Attribute("value", quality);
        }

        // The groups switch as a segmented control like the others: points for good and bad groups off, the default ones, or the expert's own while they differ.
        private IUiComponent GroupSwitch(SaveAcquisitionProfileDataV1 input)
        {
            var current = GroupChoice(input.GoodGroupScore, input.BadGroupScore);
            var choices = new List<(string Value, string LabelKey)> { (GroupsIgnored, "profile.prefer.any"), (GroupsRespected, "profile.prefer.respect") };
            if (current == GroupsCustom) choices.Add((GroupsCustom, "profile.prefer.custom"));
            var segments = choices.Select((choice, index) =>
            {
                var radio = new Input().Attribute("type", "radio").Attribute("name", "GroupPreference").Attribute("value", choice.Value)
                    .Attribute("id", "profile-groups-" + index.ToString(CultureInfo.InvariantCulture));
                if (choice.Value == current) radio.Attribute("checked", "checked");
                return (IUiComponent)new Label(radio, new Span(new Text(choice.LabelKey))).Class("ui-segment");
            }).ToList();
            return new Div(new Input().Attribute("type", "hidden").Attribute("name", "GroupPreferenceWas").Attribute("value", current),
                new Fieldset(new Legend(new Span(new Text("profile.prefer.groups")).Class("ui-field__label"), new FieldInfo("profile.prefer.groups.info", "profile-groups-info"))
                        .Class("ui-field__top"),
                    new Div(new Div([.. segments]).Class("ui-segments")).Class("ui-field__control ui-field__control--segmented")).Class("ui-field__group")).Class("ui-field");
        }

        // The one sentence under the heading of an expert part.
        private static IUiComponent ExpertInfo(string key) => new P(new Text(key)).Class("profile-expert__info");

        // One line per media type with two switches: the profile applies to it, and it is the type's default.
        private IUiComponent KindSwitches(IReadOnlyList<SelectOption> kinds, IEnumerable<AcquisitionMediaKind> applies, IEnumerable<AcquisitionMediaKind> defaults)
        {
            var on = applies.Select(kind => kind.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var standard = defaults.Select(kind => kind.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rows = new List<IUiComponent>
            {
                new Div(new Span(), new Span(new Text("profile.appliesTo")), new Span(new Text("profile.defaultFor"))).Class("profile-kinds__head").Attribute("aria-hidden", "true")
            };
            rows.AddRange(kinds.Select(kind => (IUiComponent)new Div(new Span(new ValueText(kind.Label)).Class("profile-kinds__name"),
                new Toggle(new ValueText(texts["profile.appliesTo"] + ": " + kind.Label), on.Contains(kind.Value), hidesLabel: true).Named("MediaKinds", kind.Value),
                new Toggle(new ValueText(texts["profile.defaultFor"] + ": " + kind.Label), standard.Contains(kind.Value), hidesLabel: true).Named("DefaultFor", kind.Value))
                .Class("profile-kinds__row")));
            return new Div([.. rows]).Class("profile-kinds ui-field");
        }

        private sealed class Fieldset(params IUiComponent[] children) : HtmlElement("fieldset", children);

        private sealed class Legend(params IUiComponent[] children) : HtmlElement("legend", children);

        private sealed class SelectElement(params IUiComponent[] children) : HtmlElement("select", children);

        private sealed class OptionElement(params IUiComponent[] children) : HtmlElement("option", children);

        private IUiComponent Draft(string key, string command, string value) => new Button(new Text(key)).Attribute("type", "submit").Attribute("formaction", Address("Draft"))
            .Attribute("formnovalidate", "formnovalidate").Attribute("name", command).Attribute("value", value);

        // A round arrow that moves a quality one place up or down; the first cannot go up, the last cannot go down.
        private IUiComponent Move(string key, string command, string value, bool atEnd)
        {
            var button = new Button(Glyph("chevron-down", 18)).IconOnly(texts[key]).Submit();
            if (atEnd) button.Disabled();
            return button.Attribute("title", texts[key]).Attribute("formaction", Address("Draft")).Attribute("formnovalidate", "formnovalidate").Attribute("name", command).Attribute("value", value);
        }

        private static SaveAcquisitionProfileDataV1 FromProfile(AcquisitionProfileDetailsV1 profile) => new(profile.Name, profile.MediaKinds, profile.DefaultFor, profile.AllowedQualities,
            profile.UpgradeAllowed, profile.UpgradeCutoffQuality, profile.MinimumSizeMb, profile.MaximumSizeMb, profile.Version, profile.QualityOrder,
            profile.QualityGroups, profile.FormatScores, profile.MinimumFormatScore, profile.CutoffFormatScore, profile.MinimumUpgradeFormatScore,
            profile.LanguageOrder, profile.LanguageFallback, profile.GoodGroupScore, profile.BadGroupScore, profile.BlockBadSources, profile.CodecPreference, profile.SizePreference);

        private static SaveAcquisitionProfileDataV1 NewProfile(AcquisitionMediaKind kind)
        {
            var family = AcquisitionMediaKinds.Family(kind);
            return new("", [kind], [], ProfileQualities.StartingQualities(family), ProfileQualities.StartingCutoff(family) is not null, ProfileQualities.StartingCutoff(family), null, null);
        }

        // Upgrades run while a quality to stop at is chosen; the groups switch, when it was changed, sets the points of a good and a bad group.
        private SaveAcquisitionProfileDataV1 Posted()
        {
            (int Good, int Bad) points = GroupPreference != GroupPreferenceWas && GroupPreference is GroupsIgnored or GroupsRespected
                ? GroupPreference == GroupsIgnored ? (0, 0) : (DefaultGoodGroupScore, DefaultBadGroupScore)
                : (GoodGroupScore, BadGroupScore);
            var (good, bad) = points;
            var cutoff = string.IsNullOrWhiteSpace(UpgradeCutoffQuality) ? null : UpgradeCutoffQuality;
            return new(Name ?? "", Kinds(MediaKinds), Kinds(DefaultFor), AllowedQualities ?? [], cutoff is not null, cutoff, MinimumSizeMb,
                MaximumSizeMb, Version, Terms(QualityOrder), new(QualityGroups.Select(group => new QualityGroupV1(group.Id, group.Name ?? "", Terms(group.Qualities)))),
                new(FormatScores.Where(score => score.Score != 0).Select(score => new CustomFormatScoreV1(score.FormatId, score.Score))), MinimumFormatScore, CutoffFormatScore, MinimumUpgradeFormatScore,
                Terms(LanguageOrder), LanguageFallback, good, bad, BlockBadSources, CodecPreference, SizePreference);
        }

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

        public async Task<IActionResult> OnPostDraftAsync(bool addGroup, long? removeGroupId, bool refreshGroups, string? moveUp, string? moveDown, string? addQuality, bool addQualityNow,
            string? removeQuality)
        {
            if (addGroup && QualityGroups.Count < 32) QualityGroups.Add(new GroupRow(Enumerable.Range(1, 33).Select(value => (long)value).First(id => !QualityGroups.Any(group => group.Id == id)), null, null));
            if (removeGroupId is { } removedGroup) QualityGroups.RemoveAll(group => group.Id == removedGroup);
            _advancedOpen = addGroup || removeGroupId is not null || refreshGroups;
            _input = Posted();
            await LoadDataAsync();

            // The allowed qualities in their order: one moves past its neighbour, one joins at the end, one leaves (and the stop of upgrades with it); the others follow.
            var order = DisplayOrder(_input).ToList();
            var allowed = order.Where(quality => _input.AllowedQualities.Contains(quality, StringComparer.OrdinalIgnoreCase)).ToList();
            var moved = moveUp ?? moveDown;
            if (moved is not null && allowed.IndexOf(moved) is >= 0 and var position)
            {
                var target = Math.Clamp(position + (moveUp is null ? 1 : -1), 0, allowed.Count - 1);
                (allowed[position], allowed[target]) = (allowed[target], allowed[position]);
            }

            if (addQualityNow && addQuality is not null && order.Contains(addQuality) && !allowed.Contains(addQuality)) allowed.Add(addQuality);
            if (removeQuality is not null && allowed.Count > 1 && allowed.Remove(removeQuality) && string.Equals(UpgradeCutoffQuality, removeQuality, StringComparison.OrdinalIgnoreCase))
            {
                UpgradeCutoffQuality = allowed[0];
            }

            AllowedQualities = [.. allowed];
            QualityOrder = string.Join(", ", allowed.Concat(order.Where(quality => !allowed.Contains(quality))));
            GroupPreference = GroupPreferenceWas;
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
            else if (Id != NewId || NewKind is null) throw new KeyNotFoundException("The profile was not found.");

            // Every format, page by page.
            _formats = [];
            for (var formatPage = 1; ; formatPage++)
            {
                var read = await formats.ExecuteAsync<PageResult<CustomFormatDetailsV1>>(new ReadCustomFormatsParametersV1(formatPage, 100), HttpContext.RequestAborted);
                _formats.AddRange(read.Items);
                if (read.Items.Count < read.PageSize) break;
            }

            // The figures of the release groups name the link to their rating.
            _groupFigures = await groupFigures.ExecuteAsync<ReleaseGroupFiguresV1>(new ReleaseGroupScopeParametersV1(), HttpContext.RequestAborted);
        }

        private string Address(string? handler = null)
        {
            var address = ProfilesModel.Home(Request) + "/" + (ProfileId?.ToString(CultureInfo.InvariantCulture) ?? NewId);
            if (ProfileId is null && NewKind is { } kind) address += "?kind=" + kind;
            return handler is null ? address : address + (address.Contains('?') ? "&" : "?") + "handler=" + handler;
        }

        private static string[] Terms(string? value) => value is null ? [] : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // The media types of the post; an unknown one is left out.
        private static AcquisitionMediaKind[] Kinds(string[]? values) =>
            [.. (values ?? []).Select(value => Enum.TryParse<AcquisitionMediaKind>(value, true, out var kind) && Enum.IsDefined(kind) ? kind : (AcquisitionMediaKind?)null).OfType<AcquisitionMediaKind>().Distinct()];

        private string[] DisplayOrder(SaveAcquisitionProfileDataV1 input)
        {
            var groups = input.QualityGroups.Where(group => !string.IsNullOrWhiteSpace(group.Name) && group.Qualities.Length > 0).ToArray();
            var grouped = groups.SelectMany(group => group.Qualities).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var available = ProfileQualities.For(Family).Where(quality => !grouped.Contains(quality)).Concat(groups.Select(group => group.Key)).ToArray();
            return input.QualityOrder.Select(quality => groups.FirstOrDefault(group => group.Qualities.Contains(quality, StringComparer.OrdinalIgnoreCase))?.Key ?? quality)
                .Where(available.Contains).Concat(available).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public sealed record GroupRow(long Id, string? Name, string? Qualities);
        public sealed record ScoreRow(long FormatId, int Score);
    }
}
