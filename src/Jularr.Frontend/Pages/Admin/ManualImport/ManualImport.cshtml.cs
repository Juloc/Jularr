using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Work;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Jularr.Frontend.Pages.Admin.ManualImport
{
    // A folder of any storage, or a download that waits for a person, imported by hand: choose the film or series, check what each file is, match the ones that are not clear, import the chosen.
    public sealed class ManualImportModel(FindManualImportWorksV1 find, ReadManualImportWorkV1 readWork, ReadManualImportFilesV1 readFiles, PreviewManualImportV1 preview, RunManualImportV1 run,
        ReadManualImportDownloadV1 readDownload, RunManualImportDownloadV1 runDownload, ReadStorageMountsV1 mounts, ReadStorageMountV1 mount, ReadStorageDirectoriesV1 directories, ReadStorageLocalPathsV1 localPaths,
        IAntiforgery antiforgery, UiText texts, FrontendAssets assets) : JularrPage
    {
        private const string Route = "/Admin/ManualImport";
        private const string MatchDialogId = "manual-import-match";
        private static readonly FieldHelp NoHelp = new("", "");

        private BaseDataList<ManualImportWorkV1>? _works;
        private ManualImportWorkV1? _work;
        private string? _mountName;
        private IReadOnlyList<ManualImportFileV1> _files = [];
        private bool _folderRead;
        private List<FileLine> _lines = [];
        private ManualImportResultV1? _result;
        private int? _matching;
        private BaseDataList<ManualImportWorkV1>? _candidates;
        private ManualImportWorkV1? _matchedWork;
        private string? _notice;

        [BindProperty(SupportsGet = true)]
        public string? Query { get; set; }

        [BindProperty(SupportsGet = true)]
        public long? WantedItemId { get; set; }

        [BindProperty(SupportsGet = true)]
        public long? StorageMountId { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? RelativePath { get; set; }

        // A waiting download of the queue: its folder and film or series are known.
        [BindProperty(SupportsGet = true)]
        public long? OperationId { get; set; }

        // What the form's button asked for: run, match:<line>, searchMatch, applyMatch, or nothing but showing the page again.
        [BindProperty]
        public string? Intent { get; set; }

        [BindProperty]
        public ImportTransferMode ImportTransferModeId { get; set; } = ImportTransferMode.Link;

        [BindProperty]
        public List<FileRow>? Files { get; set; }

        [BindProperty]
        public int? MatchRow { get; set; }

        [BindProperty]
        public string? MatchQuery { get; set; }

        [BindProperty]
        public long? MatchedWantedItemId { get; set; }

        [BindProperty]
        public int? MatchedSeason { get; set; }

        [BindProperty]
        public int? MatchedEpisode { get; set; }

        [BindProperty]
        public string? MatchedQualityKey { get; set; }

        // The languages the match dialog's boxes chose; the ones not ticked are not sent.
        [BindProperty]
        public string[]? MatchedAudioLanguages { get; set; }

        [BindProperty]
        public string[]? MatchedSubtitleLanguages { get; set; }

        private bool IsDownload => OperationId is not null;

        protected override void Define(Page page)
        {
            page.Title("admin.manualImport.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/ManualImport/ManualImport.entry.scss");
            page.Asset("Pages/Admin/ManualImport/ManualImport.entry.ts");
            page.Asset("Framework/Components/FolderPicker.entry.ts");
            page.Asset("Framework/Components/ActionDock.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken!;
            if (_work is null)
            {
                Search(page, csrf);
                return;
            }

            // Only a folder's title can be changed; a waiting download keeps its own and a finished import stays as it was.
            var changes = !IsDownload && _result is null;
            page.Content.Add(new ListPanel("manualImport.list.target", changes ? [new ListColumn("manualImport.search", ListRole.Main), new ListColumn("common.actions", ListRole.Actions)] : [new ListColumn("manualImport.search", ListRole.Main)],
                [TargetRow(_work, changes ? new A(Glyph("swap", 14), new Text("manualImport.change")).Class("button button--soft button--compact").Attribute("href", Address()) : null)]).Modifier(changes ? "manual-import-target" : "manual-import-target manual-import-target--fixed"));
            if (changes)
            {
                page.Content.Add(new Form<ReadManualImportDataV1>(form =>
                {
                    form.Hidden(item => item.StorageMountId);
                    form.Hidden(item => item.RelativePath);
                    form.Content(Picker());
                    form.Submit("manualImport.read");
                }, Address("Read", _work.WantedItemId), csrf, new ReadManualImportDataV1(StorageMountId ?? 0, RelativePath ?? ""), _folderRead ? null : FormError));
            }

            if (_result is { } result)
            {
                Report(page, result);
                return;
            }

            if (!_folderRead) return;
            if (_lines.Count == 0)
            {
                page.Content.Add(new Div(new IconTile(Glyph("search", 22), UiTone.Neutral), new P(new Text("manualImport.noFiles"))).Class("list-empty").Attribute("role", "status"));
                return;
            }

            FilesList(page, csrf);
        }

        // The title to import to: found by its words, chosen from the list.
        private void Search(Page page, string csrf)
        {
            page.Content.Add(new Form<ManualImportWorksParametersV1>(form =>
            {
                form.Text(item => item.Query, "manualImport.search");
                form.Submit("manualImport.find");
            }, Address("Find"), csrf, new ManualImportWorksParametersV1(Query ?? ""), FormError));
            if (_works is not { } works) return;
            var empty = new Div(new IconTile(Glyph("search", 22), UiTone.Neutral), new P(new Text("manualImport.noMatch")));
            page.Content.Add(new ListPanel("manualImport.list.works", [new ListColumn("manualImport.search", ListRole.Main), new ListColumn("common.actions", ListRole.Actions)],
                [.. works.Select(work => TargetRow(work, new A(Glyph("check", 14), new Text("manualImport.choose")).Class("button button--soft button--compact").Attribute("href", Address(wantedItemId: work.WantedItemId))))],
                empty).Modifier("manual-import-works"));
        }

        // One film or series as a row, with its kind below its name: one searched for, or the one the import is for.
        private ListRow TargetRow(ManualImportWorkV1 work, IUiComponent? action)
        {
            var identity = new ListIdentity(new IconTile(Glyph(TypeIcon(work.MediaTypeId), 18), UiTone.Accent), new Truncated(WithYear(work.CanonicalTitle, work.ReleaseYear)),
                new Text(MediaTypeLabel.Key(work.MediaTypeId, work.MediaSubtypeId)));
            return action is null ? new ListRow([identity]) : new ListRow([identity, new Div(action).Class("list-actions")]);
        }

        // The files with what each is, the way to place them, and the dialog that matches one.
        private void FilesList(Page page, string csrf)
        {
            var hidden = new List<(string Name, string Value)> { ("WantedItemId", _work!.WantedItemId.ToString(CultureInfo.InvariantCulture)) };
            if (OperationId is { } operationId) hidden.Add(("OperationId", operationId.ToString(CultureInfo.InvariantCulture)));
            else
            {
                hidden.Add(("StorageMountId", (StorageMountId ?? 0).ToString(CultureInfo.InvariantCulture)));
                hidden.Add(("RelativePath", RelativePath ?? ""));
            }

            var parts = new List<IUiComponent>();
            if (FormError is not null) parts.Add(new P(ErrorText.Of(FormError)).Class("ui-page-error").Attribute("role", "alert"));
            if (_notice is not null) parts.Add(new P(new Text(_notice)).Class("ui-page-error").Attribute("role", "alert"));
            parts.Add(Tools());
            var showsEpisode = _lines.Any(line => line.MediaType == MediaType.Series);
            var columns = new List<ListColumn>
            {
                new("manualImport.col.file", ListRole.Main), new("table.status", ListRole.Status), new("manualImport.col.series", ListRole.Data, 2), new("manualImport.col.episode"),
                new("manualImport.col.quality", ListRole.Data, 1), new("manualImport.col.language", ListRole.Data, 2), new("manualImport.col.size", ListRole.Data, 1), new("common.actions", ListRole.Actions)
            };
            parts.Add(new ListPanel("manualImport.list.files", columns, [.. _lines.Select((line, index) => LineRow(line, index, showsEpisode))]).Modifier("manual-import-files"));
            if (_matching is { } matching && matching >= 0 && matching < _lines.Count) parts.Add(MatchDialog(_lines[matching], matching));
            page.Content.Add(new PostForm(Route, csrf, hidden, [.. parts]).Class("manual-import").Attribute("id", "manual-import"));

            // The dock: where else to look, and the one thing to do with the chosen files.
            var more = IsDownload ? new A(Glyph("activity", 16), new Span(new Text("manualImport.backToQueue")).Class("action-dock__text")).Class("button").Attribute("href", "/Admin/Activity")
                : new A(Glyph("folder", 16), new Span(new Text("manualImport.changeFolder")).Class("action-dock__text")).Class("button").Attribute("href", Address(wantedItemId: _work.WantedItemId));
            var runChosen = new Button(Glyph("download", 16), new Span(new Text("manualImport.runSelected")).Class("action-dock__text")).Primary().Submit().Attribute("form", "manual-import")
                .Attribute("name", "Intent").Attribute("value", "run");

            // A waiting download leaves the queue by itself once every file is imported; the person can end it sooner, with the chosen files or without any.
            if (!IsDownload)
            {
                page.Content.Add(new ActionDock(assets, "manualImport.actions", [more], [runChosen]));
                return;
            }

            IUiComponent Finish() => new Button(Glyph("check", 16), new Span(new Text("manualImport.finish")).Class("action-dock__text")).Submit().Attribute("form", "manual-import")
                .Attribute("name", "Intent").Attribute("value", "finish");
            page.Content.Add(new ActionDock(assets, "manualImport.actions", [more, Finish()], [runChosen, Finish()]));
        }

        // How the chosen files are placed, and one switch for all of them.
        private IUiComponent Tools()
        {
            var all = new Toggle("manualImport.selectAll", _lines.All(line => line.Choice.Imports));
            all.Control.Attribute("data-manual-all", "");
            var mode = new Form<RunManualImportDataV1>(form =>
                form.Select(item => item.ImportTransferModeId, "manualImport.mode", Enum.GetValues<ImportTransferMode>().Select(value => new SelectOption(((byte)value).ToString(CultureInfo.InvariantCulture), texts["manualImport.mode." + value]))),
                data: new RunManualImportDataV1(StorageMountId ?? 0, RelativePath ?? "", ImportTransferModeId, new BaseDataList<ManualImportChoiceV1>([])), local: true);
            return new Div(mode, new Div(all).Class("manual-import__all")).Class("manual-import__tools");
        }

        private ListRow LineRow(FileLine line, int index, bool showsEpisode)
        {
            var name = "Files[" + index.ToString(CultureInfo.InvariantCulture) + "].";
            var choice = line.Choice;
            var needsEpisode = line.MediaType == MediaType.Series && (choice.Season is null || choice.Episode is null);

            // The switch that chooses the file, and everything the form must remember about it.
            var select = new Toggle("manualImport.select", choice.Imports);
            select.Control.Attribute("name", name + "Imports").Attribute("data-dock-select", "");
            var state = new List<IUiComponent>
            {
                Hidden(name + "Path", choice.Path), Hidden(name + "WantedItemId", choice.WantedItemId?.ToString(CultureInfo.InvariantCulture) ?? ""),
                Hidden(name + "Season", choice.Season?.ToString(CultureInfo.InvariantCulture) ?? ""), Hidden(name + "Episode", choice.Episode?.ToString(CultureInfo.InvariantCulture) ?? ""),
                Hidden(name + "QualityKey", choice.QualityKey ?? ""), Hidden(name + "AudioLanguages", string.Join(',', choice.AudioLanguages)),
                Hidden(name + "SubtitleLanguages", string.Join(',', choice.SubtitleLanguages))
            };
            var folder = Path.GetDirectoryName(choice.Path.Replace('/', Path.DirectorySeparatorChar))?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
            var sub = line.TargetName is { } target ? new Truncated("→ " + target) : new Truncated(folder.Length > 0 ? folder : "/");
            var identity = new ListIdentity(new IconTile(Glyph("play", 18), UiTone.Accent), new Truncated(Path.GetFileName(choice.Path)), sub);
            var main = new Div(new Div([select, Hidden(name + "Imports", "false"), .. state]).Class("manual-import__select"), identity).Class("manual-import__main");

            var episode = line.MediaType == MediaType.Series ? EpisodeLabel(choice.Season, choice.Episode) : "–";
            var quality = choice.QualityKey ?? line.File.QualityKey ?? "–";
            var language = LanguageText(choice.AudioLanguages, choice.SubtitleLanguages, line.File.Languages);
            var size = ByteSize.Format(line.File.SizeBytes);
            var match = new Button(Glyph("link", 14), new Text("manualImport.match")).Compact().Soft().Submit().Attribute("name", "Intent")
                .Attribute("value", "match:" + index.ToString(CultureInfo.InvariantCulture)).Attribute("formnovalidate", "formnovalidate");
            var caption = new List<IUiComponent> { ListPanel.Fact("manualImport.col.series", new ValueText(line.WorkTitle)) };
            if (showsEpisode && line.MediaType == MediaType.Series) caption.Add(ListPanel.Fact("manualImport.col.episode", new ValueText(episode)));
            caption.Add(ListPanel.Fact("manualImport.col.quality", new ValueText(quality)));
            caption.Add(ListPanel.Fact("manualImport.col.language", new ValueText(language)));
            caption.Add(ListPanel.Fact("manualImport.col.size", new ValueText(size)));
            var row = new ListRow([main, LineState(choice.Imports, needsEpisode, line.File.IsHandled), new Truncated(line.WorkTitle), Plain(episode), Plain(quality), Plain(language), Plain(size), new Div(match).Class("list-actions")], caption,
                Path.GetFileName(choice.Path));
            return needsEpisode ? row.Tinted(UiTone.Warning) : row;
        }

        // What the language column says: the languages a person chose, else the ones the file name states; chosen subtitles follow.
        private string LanguageText(string[] audio, string[] subtitles, string? fromName)
        {
            var spoken = audio.Length > 0 ? string.Join('+', audio.Select(ShortLanguage)) : fromName ?? "–";
            return subtitles.Length > 0 ? string.Format(CultureInfo.CurrentCulture, texts["manualImport.languageWithSubtitles"], spoken, string.Join('+', subtitles.Select(ShortLanguage))) : spoken;
        }

        private static string ShortLanguage(string code) => FileLanguages.Known.Where(known => known.Three == code).Select(known => known.Two.ToUpperInvariant()).FirstOrDefault(code.ToUpperInvariant())!;

        private IUiComponent LineState(bool chosen, bool needsEpisode, bool handled = false) => needsEpisode ? new StateChip(new Text("manualImport.state.needs"), UiTone.Warning, Glyph("alert", 14))
            : handled && !chosen ? new StateChip(new Text("manualImport.state.imported"), UiTone.Success, Glyph("check", 14))
            : chosen ? new StateChip(new Text("manualImport.state.ready"), UiTone.Success, Glyph("check", 14)) : new StateChip(new Text("manualImport.state.left"), UiTone.Neutral, Glyph("close", 14));

        // What one file is: its film or series, the numbers and the quality; searched, chosen, applied, all on the server.
        private IUiComponent MatchDialog(FileLine line, int index)
        {
            var current = new SelectOption(line.WantedItemId.ToString(CultureInfo.InvariantCulture), line.WorkTitle);
            var choices = new List<SelectOption> { current };
            var types = new Dictionary<long, MediaType> { [line.WantedItemId] = line.MediaType };
            foreach (var work in _candidates ?? new BaseDataList<ManualImportWorkV1>([]))
            {
                if (types.TryAdd(work.WantedItemId, work.MediaTypeId)) choices.Add(new SelectOption(work.WantedItemId.ToString(CultureInfo.InvariantCulture), WithYear(work.CanonicalTitle, work.ReleaseYear) + " · " + texts[MediaTypeLabel.Key(work.MediaTypeId, work.MediaSubtypeId)]));
            }

            if (_matchedWork is { } kept && types.TryAdd(kept.WantedItemId, kept.MediaTypeId)) choices.Add(new SelectOption(kept.WantedItemId.ToString(CultureInfo.InvariantCulture), WithYear(kept.CanonicalTitle, kept.ReleaseYear)));
            var chosen = MatchedWantedItemId ?? line.WantedItemId;
            var type = types.GetValueOrDefault(chosen, line.MediaType);
            var qualities = new List<SelectOption> { new("", texts["manualImport.qualityFromName"]) };
            qualities.AddRange(ProfileQualities.For(type, null).Select(key => new SelectOption(key, key)));
            var data = new ManualImportMatchV1(MatchQuery, chosen, MatchedSeason ?? line.Choice.Season, MatchedEpisode ?? line.Choice.Episode, MatchedQualityKey ?? line.Choice.QualityKey,
                MatchedAudioLanguages ?? [], MatchedSubtitleLanguages ?? []);
            var languages = FileLanguages.Known.Select(language => new SelectOption(language.Three, WorkLanguages.Name(language.Two))).ToArray();
            var form = new Form<ManualImportMatchV1>(fields =>
            {
                fields.Content(new Div(new Strong(new Truncated(Path.GetFileName(line.Choice.Path)))).Class("manual-import__file ui-field--wide"));
                fields.Text(item => item.MatchQuery, "manualImport.matchQuery", "off", NoHelp);
                fields.Select(item => item.MatchedWantedItemId, "manualImport.search", choices, NoHelp);
                if (types.Values.Contains(MediaType.Series))
                {
                    fields.Number(item => item.MatchedSeason, "manualImport.season", NoHelp);
                    fields.Number(item => item.MatchedEpisode, "manualImport.episode", NoHelp);
                }

                fields.Select(item => item.MatchedQualityKey, "manualImport.quality", qualities, NoHelp);
                fields.Choices(item => item.MatchedAudioLanguages, "manualImport.audioLanguages", languages, NoHelp);
                fields.Choices(item => item.MatchedSubtitleLanguages, "manualImport.subtitleLanguages", languages, NoHelp);
                fields.Cancel();
                fields.Action(new Button(Glyph("search", 14), new Text("manualImport.find")).Soft().Attribute("name", "Intent").Attribute("value", "searchMatch").Attribute("formnovalidate", "formnovalidate"));
                fields.Action(new Button(Glyph("check", 14), new Text("manualImport.apply")).Primary().Attribute("name", "Intent").Attribute("value", "applyMatch"));
            }, data: data, local: true);
            return new Modal(assets, MatchDialogId, "manualImport.matchTitle", [Hidden("MatchRow", index.ToString(CultureInfo.InvariantCulture)), form], true);
        }

        // What became of each chosen file: the figures, then one row per file.
        private void Report(Page page, ManualImportResultV1 result)
        {
            var failed = result.Files.Count(file => file.FailureKey is not null);
            var left = Math.Max(0, _lines.Count - result.Files.Count);
            page.Content.Add(new KpiTiles(
                new KpiTile("manualImport.kpi.imported", Number(result.Imported), Glyph("check", 18), UiTone.Success),
                new KpiTile("manualImport.kpi.failed", Number(failed), Glyph("alert", 18), failed > 0 ? UiTone.Danger : UiTone.Neutral),
                new KpiTile("manualImport.kpi.left", Number(left), Glyph("close", 18), UiTone.Neutral)).Strip());
            var lines = _lines.ToDictionary(line => line.Choice.Path, StringComparer.Ordinal);
            var rows = result.Files.Select(file =>
            {
                lines.TryGetValue(file.Path, out var line);
                var language = LanguageText(file.AudioLanguages, file.SubtitleLanguages, line?.File.Languages);
                var identity = new ListIdentity(new IconTile(Glyph("play", 18), file.FailureKey is null ? UiTone.Success : UiTone.Danger), new Truncated(Path.GetFileName(file.Path)),
                    new Truncated(file.FailureKey is null && line?.TargetName is { } target ? "→ " + target : file.Path));
                var reason = file.FailureKey is { } key ? texts.Has("acquisition.outcome." + key) ? texts["acquisition.outcome." + key] : "" : "";
                var chip = file.FailureKey is null ? new StateChip(new Text("manualImport.state.imported"), UiTone.Success, Glyph("check", 14))
                    : new StateChip(new Text("manualImport.state.failed"), UiTone.Danger, Glyph("alert", 14));
                var row = new ListRow([identity, chip, new Truncated(file.WorkTitle), Plain(language), reason.Length > 0 ? new Truncated(reason) : Plain("–")],
                    [ListPanel.Fact("manualImport.col.series", new ValueText(file.WorkTitle)), ListPanel.Fact("manualImport.col.language", new ValueText(language))]);
                return file.FailureKey is null ? row : row.Tinted(UiTone.Danger);
            }).ToList();
            page.Content.Add(new ListPanel("manualImport.list.report", [new ListColumn("manualImport.col.file", ListRole.Main), new ListColumn("table.status", ListRole.Status),
                new ListColumn("manualImport.col.series", ListRole.Data, 1), new ListColumn("manualImport.col.language", ListRole.Data, 1), new ListColumn("manualImport.col.reason", ListRole.Data, 1)],
                rows).Modifier("manual-import-report"));
            var again = IsDownload ? new A(Glyph("activity", 16), new Span(new Text("manualImport.backToQueue")).Class("action-dock__text")).Class("button").Attribute("href", "/Admin/Activity")
                : new A(Glyph("folder", 16), new Span(new Text("manualImport.more")).Class("action-dock__text")).Class("button").Attribute("href", Address(wantedItemId: _work!.WantedItemId, storageMountId: StorageMountId, relativePath: RelativePath));

            // A download that was not finished keeps waiting; the rest of its files are imported from the same page.
            var rest = IsDownload && !result.FinishedDownload
                ? new A(Glyph("folder", 16), new Span(new Text("manualImport.importRest")).Class("action-dock__text")).Class("button").Attribute("href", Address(operationId: OperationId))
                : null;
            page.Content.Add(new ActionDock(assets, "manualImport.actions", rest is null ? [again] : [again, rest]));
        }

        private FolderPicker Picker()
        {
            var browse = QueryHelpers.AddQueryString(Address("Folders"), new Dictionary<string, string?>
            {
                ["storageMountId"] = StorageMountId?.ToString(CultureInfo.InvariantCulture)
            });
            var label = _mountName is not null ? _mountName + " /" + RelativePath : texts["downloadStorage.unset"];
            return new FolderPicker(assets, browse, RelativePath ?? "", nameof(ReadManualImportDataV1.RelativePath), browse, storageMountFieldName: nameof(ReadManualImportDataV1.StorageMountId),
                choosesStorage: true, selectedLabel: label);
        }

        private static string TypeIcon(MediaType type) => type == MediaType.Movie || type == MediaType.Series ? "play" : "document";

        private static string EpisodeLabel(int? season, int? episode) =>
            season is { } s && episode is { } e ? "S" + s.ToString("00", CultureInfo.InvariantCulture) + "E" + e.ToString("00", CultureInfo.InvariantCulture) : "–";

        private static string WithYear(string title, int? year) => title + (year is { } value ? " (" + value.ToString(CultureInfo.InvariantCulture) + ")" : "");

        private static IUiComponent Plain(string value) => new Span(new ValueText(value)).Class(value == "–" ? "list-value list-value--dim" : "list-value");

        private static IUiComponent Number(int value) => new ValueText(value.ToString("N0", CultureInfo.CurrentCulture));

        private IUiComponent Glyph(string name, int size = 20) => new Icon(name, assets, size);

        private static Input Hidden(string name, string value) => (Input)new Input().Attribute("type", "hidden").Attribute("name", name).Attribute("value", value);

        public async Task<IActionResult> OnGetAsync()
        {
            if (!texts.Can("acquisition.manage", "Acquisition")) return LocalRedirect("/Admin/Activity");
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        public IActionResult OnPostFind()
        {
            NoHint();
            return LocalRedirect(Address(query: Query));
        }

        public IActionResult OnPostRead()
        {
            NoHint();
            return LocalRedirect(Address(wantedItemId: WantedItemId, storageMountId: StorageMountId is > 0 ? StorageMountId : null, relativePath: StorageMountId is > 0 ? RelativePath : null));
        }

        // The files form: every button of it comes here with its intent; the files and their choices were read again by LoadDataAsync.
        public async Task<IActionResult> OnPostAsync()
        {
            await LoadDataAsync();
            var ct = HttpContext.RequestAborted;
            var intent = Intent ?? "";
            if (intent is "run" or "finish") return await RunAsync(intent == "finish", ct);

            if (intent.StartsWith("match:", StringComparison.Ordinal) && int.TryParse(intent["match:".Length..], CultureInfo.InvariantCulture, out var line) && line >= 0 && line < _lines.Count)
            {
                var current = _lines[line];
                _matching = line;
                MatchedWantedItemId = null;
                MatchedSeason = current.Choice.Season;
                MatchedEpisode = current.Choice.Episode;
                MatchedQualityKey = current.Choice.QualityKey;
                MatchedAudioLanguages = current.Choice.AudioLanguages;
                MatchedSubtitleLanguages = current.Choice.SubtitleLanguages;
                MatchQuery = null;
            }
            else if (intent == "searchMatch" && MatchRow is { } searching && searching >= 0 && searching < _lines.Count)
            {
                _matching = searching;
                if (!string.IsNullOrWhiteSpace(MatchQuery)) _candidates = await find.ExecuteAsync<BaseDataList<ManualImportWorkV1>>(new ManualImportWorksParametersV1(MatchQuery.Trim()), ct);
                if (MatchedWantedItemId is { } kept && kept != _lines[searching].WantedItemId && _candidates?.All(work => work.WantedItemId != kept) != false)
                {
                    _matchedWork = await readWork.ExecuteAsync<ManualImportWorkV1>(new ManualImportParametersV1(kept), ct);
                }
            }
            else if (intent == "applyMatch" && MatchRow is { } applying && applying >= 0 && applying < _lines.Count)
            {
                await ApplyMatchAsync(applying, ct);
            }

            PreparePage();
            return Page();
        }

        // The match joins the file's choices; the whole list is read again so the library's names follow.
        private async Task ApplyMatchAsync(int index, CancellationToken ct)
        {
            var current = _lines[index];
            var target = MatchedWantedItemId ?? current.WantedItemId;
            var type = target == current.WantedItemId ? current.MediaType : (await readWork.ExecuteAsync<ManualImportWorkV1>(new ManualImportParametersV1(target), ct)).MediaTypeId;
            var isSeries = type == MediaType.Series;
            var quality = string.IsNullOrWhiteSpace(MatchedQualityKey) || !ProfileQualities.For(type, null).Contains(MatchedQualityKey) ? null : MatchedQualityKey;
            var changed = new FileRow(current.Choice.Path, true, isSeries ? MatchedSeason : null, isSeries ? MatchedEpisode : null, target == _work!.WantedItemId ? null : target, quality,
                string.Join(',', MatchedAudioLanguages ?? []), string.Join(',', MatchedSubtitleLanguages ?? []));
            Files = [.. _lines.Select((line, position) => position == index ? changed : ToRow(line.Choice))];
            _lines = await BuildLinesAsync(ct);
            _matching = null;
        }

        private static FileRow ToRow(ManualImportChoiceV1 choice) => new(choice.Path, choice.Imports, choice.Season, choice.Episode, choice.WantedItemId, choice.QualityKey,
            string.Join(',', choice.AudioLanguages), string.Join(',', choice.SubtitleLanguages));

        // The chosen files go in; a series file without its numbers is first asked for.
        private async Task<IActionResult> RunAsync(bool finishes, CancellationToken ct)
        {
            if (_lines.Any(line => line.Choice.Imports && line.MediaType == MediaType.Series && (line.Choice.Season is null || line.Choice.Episode is null)))
            {
                _notice = "manualImport.incomplete";
                PreparePage();
                return Page();
            }

            var choices = new BaseDataList<ManualImportChoiceV1>(_lines.Select(line => line.Choice));
            _result = OperationId is { } operationId
                ? await runDownload.ExecuteAsync<ManualImportResultV1>(new ActivityOperationParametersV1(operationId), new RunManualImportDownloadDataV1(ImportTransferModeId, choices, finishes), ct)
                : await run.ExecuteAsync<ManualImportResultV1>(new ManualImportParametersV1(_work!.WantedItemId), new RunManualImportDataV1(StorageMountId!.Value, RelativePath ?? "", ImportTransferModeId, choices), ct);
            Hint("manualImport.done", _result.Imported);
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnGetFoldersAsync(long? storageMountId, string? path, string? afterName, int mountPage = 1)
        {
            var content = await StorageBrowser.FragmentAsync(Address("Folders"), storageMountId, path, afterName, mountPage, false, mounts, mount, directories, localPaths, texts, HttpContext.RequestAborted);
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            content.WriteTo(writer, HtmlEncoder.Default, texts);
            Response.Headers.CacheControl = "no-store";
            return Content(writer.ToString(), "text/html");
        }

        protected override async Task LoadDataAsync()
        {
            var ct = HttpContext.RequestAborted;
            if (OperationId is { } operationId)
            {
                var download = await readDownload.ExecuteAsync<ManualImportDownloadV1>(new ActivityOperationParametersV1(operationId), default, ct);
                WantedItemId = download.WantedItemId;
                _files = download.Files;
                _folderRead = true;
            }

            if (WantedItemId is not { } wantedItemId)
            {
                if (!string.IsNullOrWhiteSpace(Query)) _works = await find.ExecuteAsync<BaseDataList<ManualImportWorkV1>>(new ManualImportWorksParametersV1(Query.Trim()), ct);
                return;
            }

            _work = await readWork.ExecuteAsync<ManualImportWorkV1>(new ManualImportParametersV1(wantedItemId), ct);
            if (OperationId is null && StorageMountId is { } mountId && mountId > 0)
            {
                _mountName = (await mount.ExecuteAsync<StorageMountDetailsV1>(new StorageMountParametersV1(mountId), ct)).Name;
                if (RelativePath is { } path)
                {
                    _files = (await readFiles.ExecuteAsync<ManualImportFolderV1>(new ManualImportParametersV1(wantedItemId), new ReadManualImportDataV1(mountId, path), ct)).Files;
                    _folderRead = true;
                }
            }

            if (_folderRead) _lines = await BuildLinesAsync(ct);
        }

        // Every file with its choice: what a person set before, else what its name says; the names follow the choices through the library's own rules.
        private async Task<List<FileLine>> BuildLinesAsync(CancellationToken ct)
        {
            var posted = (Files ?? []).Where(row => row.Path is not null).GroupBy(row => row.Path!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var isMovie = _work!.MediaTypeId == MediaType.Movie;

            // A film takes its largest video; a series every file whose name already says which episode it is.
            // A file of a waiting download that was imported already is not chosen again.
            var open = _files.Where(file => !file.IsHandled).ToArray();
            var largest = open.Length == 0 ? null : open.MaxBy(file => file.SizeBytes)!.Path;
            var choices = _files.Select(file => posted.TryGetValue(file.Path, out var row) ? new ManualImportChoiceV1(file.Path, row.Imports, row.Season, row.Episode, row.WantedItemId, BlankToNull(row.QualityKey),
                    Codes(row.AudioLanguages), Codes(row.SubtitleLanguages))
                : new ManualImportChoiceV1(file.Path, !file.IsHandled && (isMovie ? file.Path == largest : file.Season is not null && file.Episode is not null), file.Season, file.Episode)).ToList();

            // Until a person changed something the folder's own proposals are the names.
            Dictionary<string, ManualImportNameV1> names = [];
            if (posted.Count > 0 && choices.Count > 0)
            {
                var named = await preview.ExecuteAsync<BaseDataList<ManualImportNameV1>>(new ManualImportParametersV1(_work.WantedItemId), new PreviewManualImportDataV1(new BaseDataList<ManualImportChoiceV1>(choices)), ct);
                names = named.ToDictionary(name => name.Path, StringComparer.Ordinal);
            }

            return [.. _files.Zip(choices, (file, choice) => names.TryGetValue(file.Path, out var name)
                ? new FileLine(file, choice, name.WantedItemId, name.WorkTitle, name.MediaTypeId, name.TargetName)
                : new FileLine(file, choice, _work.WantedItemId, WithYear(_work.CanonicalTitle, _work.ReleaseYear), _work.MediaTypeId, file.TargetName))];
        }

        private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        // The codes of a hidden field, as the language catalog knows them.
        private static string[] Codes(string? value) => [.. (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(code => FileLanguages.Known.Any(known => known.Three == code))];

        private string Address(string? handler = null, long? wantedItemId = null, long? storageMountId = null, string? relativePath = null, string? query = null, long? operationId = null) =>
            QueryHelpers.AddQueryString(Route, new Dictionary<string, string?>
            {
                ["handler"] = handler,
                ["wantedItemId"] = (wantedItemId ?? (handler is null ? null : WantedItemId))?.ToString(CultureInfo.InvariantCulture),
                ["storageMountId"] = storageMountId?.ToString(CultureInfo.InvariantCulture),
                ["relativePath"] = relativePath,
                ["query"] = query,
                ["operationId"] = operationId?.ToString(CultureInfo.InvariantCulture)
            });

        // What the form sends of one file.
        public sealed record FileRow(string? Path, bool Imports, int? Season, int? Episode, long? WantedItemId, string? QualityKey, string? AudioLanguages = null, string? SubtitleLanguages = null);

        // A file with what a person chose for it and the film or series that choice leads to.
        private sealed record FileLine(ManualImportFileV1 File, ManualImportChoiceV1 Choice, long WantedItemId, string WorkTitle, MediaType MediaType, string? TargetName);
    }
}
