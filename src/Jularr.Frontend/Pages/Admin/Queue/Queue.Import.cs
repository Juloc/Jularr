using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Backend.Service.Admin.Storage.V1;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Work;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Jularr.Frontend.Pages.Admin.Queue
{
    // Sonarr's manual import as a dialog over the queue: a folder (or a waiting download, whose folder is known) is read, every file is recognised as its film, episode, volume or
    // chapter with its quality, language and release group, and one table shows each file green when it fits or amber/red with why. A person fixes only the flagged rows (the Change
    // dialog sets the title, numbers, quality and languages), chooses and imports: every import moves its files. With script each chosen row is imported in turn and shows what happens
    // to it and where it went, then the figures; without script the whole choice goes at once and the result follows. The dialog is the queue's address with `import=true`.
    public sealed partial class QueueModel
    {
        private const string ImportFormId = "manual-import";
        private const string ImportDialogId = "manual-import-dialog";
        private const string ImportRegionId = "manual-import-region";
        private const string MatchDialogId = "manual-import-match";
        private static readonly FieldHelp NoHelp = new("", "");

        private string? _mountName;
        private string? _folder;
        private bool _isReadOnly;
        private IReadOnlyList<ManualImportFileV1> _files = [];
        private bool _folderRead;
        private List<FileLine> _lines = [];
        private ManualImportResultV1? _result;
        private int? _matching;
        private BaseDataList<ManualImportWorkV1>? _candidates;
        private ManualImportWorkV1? _matchedWork;
        private string? _notice;
        private ServiceError? _readError;

        // The dialog is open: for any folder, or for a waiting download of the queue.
        [BindProperty(SupportsGet = true, Name = "import")]
        public bool ImportOpen { get; set; }

        [BindProperty(SupportsGet = true)]
        public long? StorageMountId { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? RelativePath { get; set; }

        // A waiting download of the queue: its folder and its title are known.
        [BindProperty(SupportsGet = true, Name = "importOperation")]
        public long? ImportOperationId { get; set; }

        // What the form's button asked for: run, finish, match:<line>, searchMatch, applyMatch, or nothing but showing the dialog again.
        [BindProperty]
        public string? Intent { get; set; }

        [BindProperty]
        public List<FileRow>? ImportFiles { get; set; }

        // The one file a script imports with ImportRunFile.
        [BindProperty]
        public string? RunPath { get; set; }

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

        private bool IsImporting => ImportOpen || ImportOperationId is not null;


        // The dialog over the queue: the folder (chosen here, or the waiting download's own), then the files, or after a run without script the result.
        private void ImportDialog(Page page, string csrf)
        {
            page.Asset("Pages/Admin/Queue/ManualImport.entry.scss");
            page.Asset("Pages/Admin/Queue/ManualImport.entry.ts");
            page.Asset("Framework/Components/FolderPicker.entry.ts");
            var body = new List<IUiComponent>();
            if (_readError is not null) body.Add(new P(ErrorText.Of(_readError)).Class("ui-page-error").Attribute("role", "alert"));
            if (ImportOperationId is not null)
            {
                if (_folder is { } folder) body.Add(new P(Glyph("folder", 16), new Text("manualImport.downloadFolder"), new ValueText(" " + folder)).Class("manual-import__folder"));
            }
            else if (_result is null)
            {
                body.Add(new Div(new Form<ReadManualImportDataV1>(form =>
                {
                    form.Hidden(item => item.StorageMountId);
                    form.Hidden(item => item.RelativePath);
                    form.Content(Picker());
                    form.Submit("manualImport.read");
                }, ImportAddress("ImportRead"), csrf, new ReadManualImportDataV1(StorageMountId ?? 0, RelativePath ?? ""), _folderRead ? null : FormError)).Class("manual-import__picker"));
                if (!_folderRead) body.Add(new P(new Text("manualImport.intro")).Class("list-notice"));
            }

            if (_result is { } result) Report(body, result);
            else if (_folderRead && _lines.Count == 0) body.Add(new Div(new IconTile(Glyph("search", 22), UiTone.Neutral), new P(new Text("manualImport.noFiles"))).Class("list-empty").Attribute("role", "status"));
            else if (_folderRead) FilesList(body, csrf);

            // Choosing the folder is a small dialog; the files take the whole width.
            page.Content.Add(new Modal(assets, ImportDialogId, "admin.manualImport.title", [new Region(ImportRegionId, [.. body])], open: true, returnTo: Address(),
                wide: _folderRead || _result is not null));
        }

        // Closing the dialog leaves the queue as it is.
        private IUiComponent CloseImport() => new A(Glyph("close", 16), new Span(new Text("common.close")).Class("action-dock__text")).Class("button").Attribute("href", Address())
            .Attribute("data-dialog-close", "");

        // The files with what each is, the switch for all, the figures a script fills while it imports, and the dialog that matches one.
        private void FilesList(List<IUiComponent> body, string csrf)
        {
            var hidden = new List<(string Name, string Value)>();
            hidden.Add(("import", "true"));
            if (ImportOperationId is { } operationId) hidden.Add(("importOperation", operationId.ToString(CultureInfo.InvariantCulture)));
            else
            {
                hidden.Add(("StorageMountId", (StorageMountId ?? 0).ToString(CultureInfo.InvariantCulture)));
                hidden.Add(("RelativePath", RelativePath ?? ""));
            }

            var parts = new List<IUiComponent>();
            if (FormError is not null) parts.Add(new P(ErrorText.Of(FormError)).Class("ui-page-error").Attribute("role", "alert"));
            if (_notice is not null) parts.Add(new P(new Text(_notice)).Class("ui-page-error").Attribute("role", "alert"));
            if (_isReadOnly) parts.Add(new P(new Text("manualImport.readOnly")).Class("ui-page-error").Attribute("role", "alert"));

            // How many fit and how many want a look, and the one switch for every file that can go.
            var flagged = _lines.Count(line => line.Problem is not null && !line.File.IsHandled);
            var all = new Toggle("manualImport.selectAll", _lines.Where(CanChoose).All(line => line.Choice.Imports));
            all.Control.Attribute("data-manual-all", "");
            parts.Add(new Div(new P(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["manualImport.found"], _lines.Count, flagged))).Class("manual-import__count"), new Div(all).Class("manual-import__all"))
                .Class("manual-import__tools"));

            // The figures of a run by script: hidden until it starts, then counted row by row.
            parts.Add(new Div(new KpiTiles(
                new KpiTile("manualImport.kpi.imported", new Span(new ValueText("0")).Attribute("data-manual-count", "imported"), Glyph("check", 18), UiTone.Success),
                new KpiTile("manualImport.kpi.failed", new Span(new ValueText("0")).Attribute("data-manual-count", "failed"), Glyph("alert", 18), UiTone.Danger),
                new KpiTile("manualImport.kpi.left", new Span(new ValueText("0")).Attribute("data-manual-count", "left"), Glyph("close", 18), UiTone.Neutral)).Strip())
                .Class("manual-import__summary").Attribute("data-manual-summary", "").Attribute("hidden", "hidden").Attribute("role", "status"));

            var columns = new List<ListColumn>
            {
                new("manualImport.col.file", ListRole.Main), new("manualImport.col.series", ListRole.Data), new(_lines.All(line => line.MediaType == MediaType.Book) ? "manualImport.col.volume" : "manualImport.col.episode", ListRole.Data),
                new("manualImport.col.quality", ListRole.Data), new("manualImport.col.language", ListRole.Data, 2), new("manualImport.col.size", ListRole.Data, 1), new("table.status", ListRole.Status),
                new("common.actions", ListRole.Actions)
            };
            parts.Add(new ListPanel("manualImport.list.files", columns, [.. _lines.Select(LineRow)]).Modifier("manual-import-files"));

            // The states a script shows in a row while it imports.
            parts.Add(new Div(new StateChip(new Text("manualImport.state.waiting"), UiTone.Neutral, Glyph("clock", 14))).Attribute("data-manual-chip", "waiting"));
            parts.Add(new Div(new StateChip(new Text("manualImport.state.moving"), UiTone.Info, Glyph("sync", 14), spinning: true)).Attribute("data-manual-chip", "moving"));
            parts.Add(new Div(new StateChip(new Text("manualImport.state.failed"), UiTone.Danger, Glyph("alert", 14))).Attribute("data-manual-chip", "failed"));
            if (_matching is { } matching && matching >= 0 && matching < _lines.Count) parts.Add(MatchDialog(_lines[matching], matching));

            // The texts a script shows while it imports.
            var form = new PostForm(ImportAddress("Import"), csrf, hidden, [.. parts]).Class("manual-import").Attribute("id", ImportFormId)
                .Attribute("data-manual-run", ImportAddress("ImportRunFile")).Attribute("data-text-waiting", texts["manualImport.state.waiting"]).Attribute("data-text-moving", texts["manualImport.state.moving"])
                .Attribute("data-text-progress", texts["manualImport.progress"]).Attribute("data-text-failed", texts["manualImport.state.failed"]);
            body.Add(form);

            // The dock: back to the queue or another folder, and the one thing to do with the chosen files.
            var more = (ImportOperationId is not null) ? CloseImport()
                : new A(Glyph("folder", 16), new Span(new Text("manualImport.changeFolder")).Class("action-dock__text")).Class("button").Attribute("href", ImportAddress());
            var runChosen = new Button(Glyph("download", 16), new Span(new Text("manualImport.runSelected")).Class("action-dock__text")).Primary().Submit().Attribute("form", ImportFormId)
                .Attribute("name", "Intent").Attribute("value", "run").Attribute("data-manual-start", "");
            if (_isReadOnly) runChosen.Attribute("disabled", "disabled");

            // A waiting download leaves the queue by itself once every file is imported; the person can end it sooner, with the chosen files or without any.
            if (!(ImportOperationId is not null))
            {
                body.Add(new ActionDock(assets, "manualImport.actions", [more], [runChosen]));
                return;
            }

            IUiComponent Finish() => new Button(Glyph("check", 16), new Span(new Text("manualImport.finish")).Class("action-dock__text")).Submit().Attribute("form", ImportFormId)
                .Attribute("name", "Intent").Attribute("value", "finish");
            body.Add(new ActionDock(assets, "manualImport.actions", [more, Finish()], [runChosen, Finish()]));
        }

        // A file can be chosen once it has its title and was not imported already.
        private static bool CanChoose(FileLine line) => line.WantedItemId is not null && !line.File.IsHandled;

        private ListRow LineRow(FileLine line, int index)
        {
            var name = "ImportFiles[" + index.ToString(CultureInfo.InvariantCulture) + "].";
            var choice = line.Choice;

            // The box that chooses the file, and everything the form must remember about it.
            var select = new Input().Attribute("type", "checkbox").Attribute("name", name + "Imports").Attribute("value", "true").Attribute("data-dock-select", "").Class("list-select")
                .TextAttribute("aria-label", "manualImport.select");
            if (choice.Imports) select.Attribute("checked", "checked");
            if (!CanChoose(line)) select.Attribute("disabled", "disabled");
            var state = new List<IUiComponent>
            {
                Hidden(name + "Imports", "false"), Hidden(name + "Path", choice.Path), Hidden(name + "WantedItemId", choice.WantedItemId?.ToString(CultureInfo.InvariantCulture) ?? ""),
                Hidden(name + "Season", choice.Season?.ToString(CultureInfo.InvariantCulture) ?? ""), Hidden(name + "Episode", choice.Episode?.ToString(CultureInfo.InvariantCulture) ?? ""),
                Hidden(name + "QualityKey", choice.QualityKey ?? ""), Hidden(name + "AudioLanguages", string.Join(',', choice.AudioLanguages)),
                Hidden(name + "SubtitleLanguages", string.Join(',', choice.SubtitleLanguages))
            };

            // Under the name: why it wants a look, else where it goes, else its folder.
            var folder = Path.GetDirectoryName(choice.Path.Replace('/', Path.DirectorySeparatorChar))?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
            var reason = Reason(line);
            var sub = line.TargetName is { } target && reason is null ? new Truncated("→ " + target) : new Truncated(folder.Length > 0 ? folder : "/");
            var identity = new ListIdentity(new IconTile(Glyph(TypeIcon(line.MediaType), 18), UiTone.Accent), new Truncated(Path.GetFileName(choice.Path)), sub,
                reason is null ? null : new Span(new ValueText(reason)).Attribute("data-manual-note", ""), new Div([select, .. state]).Class("manual-import__select"));
            var main = new Div(identity).Class("manual-import__main").Attribute("data-manual-path", choice.Path);

            // The title and the numbers: a button where they are missing, the plain value otherwise.
            var fix = Fix(choice.Path);
            IUiComponent work = line.WorkTitle is { } title ? new Truncated(title) : fix("manualImport.chooseWork");
            var numbers = line.WantedItemId is null ? "–" : line.MediaType switch
            {
                MediaType.Series => EpisodeLabel(choice.Season, choice.Episode),
                MediaType.Book => VolumeLabel(choice.Season, choice.Episode),
                _ => "–"
            };
            IUiComponent episode = line.Problem is "episode_unknown" or "volume_unknown" ? fix(line.Problem == "episode_unknown" ? "manualImport.chooseEpisode" : "manualImport.chooseVolume") : Plain(numbers);
            var quality = choice.QualityKey ?? line.File.QualityKey ?? "–";
            var language = LanguageText(choice.AudioLanguages, choice.SubtitleLanguages, line.File.Languages);
            var size = ByteSize.Format(line.File.SizeBytes);
            var edit = new Button(Glyph("pencil", 14)).Compact().Soft().Submit().Attribute("name", "Intent").Attribute("value", "match:" + choice.Path)
                .Attribute("formnovalidate", "formnovalidate").TextAttribute("aria-label", "manualImport.match").TextAttribute("title", "manualImport.match");

            // The phone card's facts: the title, the numbers, quality with release group, language and size.
            var qualityText = quality + (line.File.ReleaseGroup is { } group ? " · " + group : "");
            var caption = new List<IUiComponent> { ListPanel.Fact("manualImport.col.series", line.WorkTitle is { } known ? new ValueText(known) : fix("manualImport.chooseWork")) };
            if (line.MediaType == MediaType.Series) caption.Add(ListPanel.Fact("manualImport.col.episode", episode));
            if (line.MediaType == MediaType.Book) caption.Add(ListPanel.Fact("manualImport.col.volume", episode));
            caption.Add(ListPanel.Fact("manualImport.col.quality", new ValueText(qualityText)));
            caption.Add(ListPanel.Fact("manualImport.col.language", new ValueText(language)));
            caption.Add(ListPanel.Fact("manualImport.col.size", new ValueText(size)));
            var qualityCell = line.File.ReleaseGroup is { } releaseGroup ? new Div(Plain(quality), new Span(new ValueText(releaseGroup)).Class("list-value list-value--dim manual-import__group")) : Plain(quality);
            var row = new ListRow([main, work, episode, qualityCell, Plain(language), Plain(size), new Div(LineState(line)).Attribute("data-manual-status", ""), new Div(edit).Class("list-actions")],
                caption, Path.GetFileName(choice.Path));
            return line.File.IsHandled ? row : line.Problem is null ? row : row.Tinted(line.WantedItemId is null ? UiTone.Danger : UiTone.Warning);
        }

        // The button of a missing title or number, which opens the match dialog of its row.
        private Func<string, IUiComponent> Fix(string path) => labelKey => new Button(Glyph("alert", 14), new Text(labelKey)).Compact().Soft().Submit().Attribute("name", "Intent")
            .Attribute("value", "match:" + path).Attribute("formnovalidate", "formnovalidate");

        // Why a file wants a look, in plain words: what the import of a download said, else what the folder's reading found.
        private string? Reason(FileLine line)
        {
            if (line.File.IsHandled) return null;
            if (line.File.UnplacedReason is { } unplaced && !line.Choice.Imports) return FileReason(unplaced);
            return line.Problem is { } problem ? texts["manualImport.problem." + problem] : null;
        }

        // What the language column says: the languages a person chose, else the ones the file name states; chosen subtitles follow.
        private string LanguageText(string[] audio, string[] subtitles, string? fromName)
        {
            var spoken = audio.Length > 0 ? string.Join('+', audio.Select(ShortLanguage)) : fromName ?? "–";
            return subtitles.Length > 0 ? string.Format(CultureInfo.CurrentCulture, texts["manualImport.languageWithSubtitles"], spoken, string.Join('+', subtitles.Select(ShortLanguage))) : spoken;
        }

        private static string ShortLanguage(string code) => FileLanguages.Known.Where(known => known.Three == code).Select(known => known.Two.ToUpperInvariant()).FirstOrDefault(code.ToUpperInvariant())!;

        // Green when it fits, amber when it wants a look, red when it has no title; a file of the download imported already is done.
        private IUiComponent LineState(FileLine line) => line.File.IsHandled ? new StateChip(new Text("manualImport.state.imported"), UiTone.Success, Glyph("check", 14))
            : line.Problem is null && line.File.UnplacedReason is null ? new StateChip(new Text("manualImport.state.ready"), UiTone.Success, Glyph("check", 14))
            : new StateChip(new Text("manualImport.state.check"), line.WantedItemId is null ? UiTone.Danger : UiTone.Warning, Glyph("alert", 14));

        // What one file is: its film, series or book, the numbers, the quality and the languages; searched, chosen, applied, all on the server. A book's file is matched among books,
        // a video among films and series.
        private IUiComponent MatchDialog(FileLine line, int index)
        {
            var isBook = line.MediaType == MediaType.Book;
            var choices = new List<SelectOption>();
            var types = new Dictionary<long, (MediaType Type, MediaSubtype? Subtype)>();
            if (line.WantedItemId is { } own)
            {
                choices.Add(new SelectOption(own.ToString(CultureInfo.InvariantCulture), line.WorkTitle ?? ""));
                types[own] = (line.MediaType, line.MediaSubtype);
            }

            foreach (var work in (_candidates ?? new BaseDataList<ManualImportWorkV1>([])).Where(work => (work.MediaTypeId == MediaType.Book) == isBook))
            {
                if (types.TryAdd(work.WantedItemId, (work.MediaTypeId, work.MediaSubtypeId))) choices.Add(new SelectOption(work.WantedItemId.ToString(CultureInfo.InvariantCulture), WithYear(work.CanonicalTitle, work.ReleaseYear) + " · " + texts[MediaTypeLabel.Key(work.MediaTypeId, work.MediaSubtypeId)]));
            }

            if (_matchedWork is { } kept && (kept.MediaTypeId == MediaType.Book) == isBook && types.TryAdd(kept.WantedItemId, (kept.MediaTypeId, kept.MediaSubtypeId)))
            {
                choices.Add(new SelectOption(kept.WantedItemId.ToString(CultureInfo.InvariantCulture), WithYear(kept.CanonicalTitle, kept.ReleaseYear)));
            }

            var chosen = MatchedWantedItemId ?? line.WantedItemId;
            var (type, subtype) = chosen is { } id && types.TryGetValue(id, out var known) ? known : (line.MediaType, line.MediaSubtype);
            var qualities = new List<SelectOption> { new("", texts[isBook ? "manualImport.qualityFromFormat" : "manualImport.qualityFromName"]) };
            qualities.AddRange(ProfileQualities.For(type, subtype).Select(key => new SelectOption(key, key)));
            var data = new ManualImportMatchV1(MatchQuery, chosen, MatchedSeason ?? line.Choice.Season, MatchedEpisode ?? line.Choice.Episode, MatchedQualityKey ?? line.Choice.QualityKey,
                MatchedAudioLanguages ?? [], MatchedSubtitleLanguages ?? []);
            var languages = FileLanguages.Known.Select(language => new SelectOption(language.Three, WorkLanguages.Name(language.Two))).ToArray();
            var form = new Form<ManualImportMatchV1>(fields =>
            {
                fields.Content(new Div(new Strong(new Truncated(Path.GetFileName(line.Choice.Path)))).Class("manual-import__file ui-field--wide"));
                fields.Text(item => item.MatchQuery, "manualImport.matchQuery", "off", NoHelp);
                if (choices.Count > 0) fields.Select(item => item.MatchedWantedItemId, "manualImport.search", choices, NoHelp);
                if (isBook)
                {
                    // A volume, a manga's chapter (with its volume, else the one the Work gives it), or neither for the whole Work.
                    fields.Number(item => item.MatchedSeason, "manualImport.volume", NoHelp);
                    if (types.Values.Any(value => value.Subtype == MediaSubtype.Manga)) fields.Number(item => item.MatchedEpisode, "manualImport.chapter", NoHelp);
                }
                else if (line.MediaType == MediaType.Series || types.Values.Any(value => value.Type == MediaType.Series))
                {
                    fields.Number(item => item.MatchedSeason, "manualImport.season", NoHelp);
                    fields.Number(item => item.MatchedEpisode, "manualImport.episode", NoHelp);
                }

                fields.Select(item => item.MatchedQualityKey, "manualImport.quality", qualities, NoHelp);
                if (isBook)
                {
                    fields.Choices(item => item.MatchedAudioLanguages, "manualImport.bookLanguage", languages, NoHelp);
                }
                else
                {
                    fields.Choices(item => item.MatchedAudioLanguages, "manualImport.audioLanguages", languages, NoHelp);
                    fields.Choices(item => item.MatchedSubtitleLanguages, "manualImport.subtitleLanguages", languages, NoHelp);
                }
                fields.Cancel();
                fields.Action(new Button(Glyph("search", 14), new Text("manualImport.find")).Soft().Attribute("name", "Intent").Attribute("value", "searchMatch").Attribute("formnovalidate", "formnovalidate"));
                if (choices.Count > 0) fields.Action(new Button(Glyph("check", 14), new Text("manualImport.apply")).Primary().Attribute("name", "Intent").Attribute("value", "applyMatch"));
            }, data: data, local: true);
            return new Modal(assets, MatchDialogId, "manualImport.matchTitle", [Hidden("MatchRow", index.ToString(CultureInfo.InvariantCulture)), form], true);
        }

        // What became of each chosen file without script: the figures, then one row per file with where it went or why not.
        private void Report(List<IUiComponent> body, ManualImportResultV1 result)
        {
            var failed = result.Files.Count(file => file.FailureKey is not null);
            var left = Math.Max(0, _lines.Count(CanChoose) - result.Files.Count);
            var report = new List<IUiComponent>();
            report.Add(new KpiTiles(
                new KpiTile("manualImport.kpi.imported", Number(result.Imported), Glyph("check", 18), UiTone.Success),
                new KpiTile("manualImport.kpi.failed", Number(failed), Glyph("alert", 18), failed > 0 ? UiTone.Danger : UiTone.Neutral),
                new KpiTile("manualImport.kpi.left", Number(left), Glyph("close", 18), UiTone.Neutral)).Strip());
            var lines = _lines.ToDictionary(line => line.Choice.Path, StringComparer.Ordinal);
            var rows = result.Files.Select(file =>
            {
                lines.TryGetValue(file.Path, out var line);
                var language = LanguageText(file.AudioLanguages, file.SubtitleLanguages, line?.File.Languages);
                var identity = new ListIdentity(new IconTile(Glyph(TypeIcon(line?.MediaType ?? MediaType.Movie), 18), file.FailureKey is null ? UiTone.Success : UiTone.Danger), new Truncated(Path.GetFileName(file.Path)),
                    new Truncated(OutcomeText(file)));
                var row = new ListRow([identity, new Truncated(file.WorkTitle), Plain(language), OutcomeChip(file)],
                    [ListPanel.Fact("manualImport.col.series", new ValueText(file.WorkTitle)), ListPanel.Fact("manualImport.col.language", new ValueText(language))]);
                return file.FailureKey is null ? row : row.Tinted(UiTone.Danger);
            }).ToList();
            report.Add(new ListPanel("manualImport.list.report", [new ListColumn("manualImport.col.file", ListRole.Main), new ListColumn("manualImport.col.series", ListRole.Data, 1),
                new ListColumn("manualImport.col.language", ListRole.Data, 1), new ListColumn("table.status", ListRole.Status)], rows).Modifier("manual-import-report"));
            body.Add(new Div([.. report]).Class("manual-import__scroll"));
            var again = (ImportOperationId is not null) ? CloseImport()
                : new A(Glyph("folder", 16), new Span(new Text("manualImport.more")).Class("action-dock__text")).Class("button").Attribute("href", ImportAddress(storageMountId: StorageMountId, relativePath: RelativePath));

            // A download that was not finished keeps waiting; the rest of its files are imported from the same page.
            var rest = (ImportOperationId is not null) && !result.FinishedDownload
                ? new A(Glyph("folder", 16), new Span(new Text("manualImport.importRest")).Class("action-dock__text")).Class("button").Attribute("href", ImportAddress(operationId: ImportOperationId))
                : null;
            body.Add(new ActionDock(assets, "manualImport.actions", rest is null ? [again] : [again, rest]));
        }

        // Where a file went, or why it did not: "moved to <path>", "copied to <path>" when its source could not be deleted, "failed: <reason>".
        private string OutcomeText(ManualImportOutcomeV1 file) => file.FailureKey is { } key
            ? string.Format(CultureInfo.CurrentCulture, texts["manualImport.failedBecause"], texts.Has("acquisition.outcome." + key) ? texts["acquisition.outcome." + key] : texts["errors.internal_error"])
            : string.Format(CultureInfo.CurrentCulture, texts[file.SourceKept ? "manualImport.copiedTo" : "manualImport.movedTo"], file.TargetPath ?? "");

        private IUiComponent OutcomeChip(ManualImportOutcomeV1 file) => file.FailureKey is null
            ? new StateChip(new Text("manualImport.state.moved"), UiTone.Success, Glyph("check", 14))
            : new StateChip(new Text("manualImport.state.failed"), UiTone.Danger, Glyph("alert", 14));

        private FolderPicker Picker()
        {
            var browse = QueryHelpers.AddQueryString(ImportAddress("ImportFolders"), new Dictionary<string, string?>
            {
                ["storageMountId"] = StorageMountId?.ToString(CultureInfo.InvariantCulture)
            });
            var label = _mountName is not null ? _mountName + " /" + RelativePath : texts["downloadStorage.unset"];
            return new FolderPicker(assets, browse, RelativePath ?? "", nameof(ReadManualImportDataV1.RelativePath), browse, storageMountFieldName: nameof(ReadManualImportDataV1.StorageMountId),
                choosesStorage: true, selectedLabel: label);
        }

        private static string TypeIcon(MediaType type) => type is MediaType.Movie or MediaType.Series ? "play" : "document";

        private static string EpisodeLabel(int? season, int? episode) =>
            season is { } s && episode is { } e ? "S" + s.ToString("00", CultureInfo.InvariantCulture) + "E" + e.ToString("00", CultureInfo.InvariantCulture) : "–";

        // A book's file is a volume, a manga chapter (with its volume when known) or the whole Work.
        private string VolumeLabel(int? volume, int? chapter)
        {
            string Numbered(string key, int number) => string.Format(CultureInfo.CurrentCulture, texts[key], number.ToString("00", CultureInfo.InvariantCulture));
            return chapter is { } c ? (volume is { } v ? Numbered("manualImport.volumeLabel", v) + " · " : "") + Numbered("manualImport.chapterLabel", c)
                : volume is { } only ? Numbered("manualImport.volumeLabel", only) : texts["manualImport.wholeWork"];
        }

        private static string WithYear(string title, int? year) => title + (year is { } value ? " (" + value.ToString(CultureInfo.InvariantCulture) + ")" : "");

        private static IUiComponent Plain(string value) => new Span(new ValueText(value)).Class(value == "–" ? "list-value list-value--dim" : "list-value");

        private static IUiComponent Number(int value) => new ValueText(value.ToString("N0", CultureInfo.CurrentCulture));

        private static Input Hidden(string name, string value) => (Input)new Input().Attribute("type", "hidden").Attribute("name", name).Attribute("value", value);

        public IActionResult OnPostImportRead()
        {
            NoHint();
            return LocalRedirect(ImportAddress(storageMountId: StorageMountId is > 0 ? StorageMountId : null, relativePath: StorageMountId is > 0 ? RelativePath : null));
        }

        // The files form: every button of it comes here with its intent; the files and their choices were read again by LoadDataAsync.
        public async Task<IActionResult> OnPostImportAsync()
        {
            ImportOpen = true;
            await LoadDataAsync();
            var ct = HttpContext.RequestAborted;
            var intent = Intent ?? "";
            if (intent is "run" or "finish") return await RunAsync(intent == "finish", ct);

            // A row is named by its file, which stays the same when files before it were moved away.
            if (intent.StartsWith("match:", StringComparison.Ordinal) && _lines.FindIndex(file => file.Choice.Path == intent["match:".Length..]) is var line and >= 0)
            {
                var current = _lines[line];
                _matching = line;
                MatchedWantedItemId = null;
                MatchedSeason = current.Choice.Season;
                MatchedEpisode = current.Choice.Episode;
                MatchedQualityKey = current.Choice.QualityKey;
                MatchedAudioLanguages = current.Choice.AudioLanguages;
                MatchedSubtitleLanguages = current.Choice.SubtitleLanguages;

                // A file without its title starts with a search by what its name says.
                MatchQuery = current.WantedItemId is null ? SearchWords(current.Choice.Path) : null;
                if (MatchQuery is { Length: > 0 } words) _candidates = await find.ExecuteAsync<BaseDataList<ManualImportWorkV1>>(new ManualImportWorksParametersV1(words), ct);
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

        // The longest words of a file's name, without its numbers and release words, as the dialog's first search.
        private static string? SearchWords(string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var cut = Regex.Match(stem, @"[ ._\-\[(](?:s\d{1,2}e\d|\d{3,4}p|(?:19|20)\d{2}|vol|v\d|ch\d|\d{1,3}[ ._\-\])])", RegexOptions.IgnoreCase);
            var words = (cut.Success && cut.Index > 0 ? stem[..cut.Index] : stem).Replace('.', ' ').Replace('_', ' ').Trim(' ', '-', '[', ']', '(', ')');
            return words.Length is > 0 and <= 200 ? words : null;
        }

        // The match joins the file's choices; the whole list is read again so the library's names follow.
        private async Task ApplyMatchAsync(int index, CancellationToken ct)
        {
            var current = _lines[index];
            if ((MatchedWantedItemId ?? current.WantedItemId) is not { } target)
            {
                _matching = index;
                return;
            }

            var (type, subtype) = (current.MediaType, current.MediaSubtype);
            if (target != current.WantedItemId)
            {
                var work = await readWork.ExecuteAsync<ManualImportWorkV1>(new ManualImportParametersV1(target), ct);
                (type, subtype) = (work.MediaTypeId, work.MediaSubtypeId);
            }

            // A series' file takes its season and episode; a book's its volume, and a manga's also its chapter.
            var isSeries = type == MediaType.Series;
            var isBook = type == MediaType.Book;
            var season = isSeries || isBook ? MatchedSeason : null;
            var episode = isSeries || isBook && subtype == MediaSubtype.Manga ? MatchedEpisode : null;
            var quality = string.IsNullOrWhiteSpace(MatchedQualityKey) || !ProfileQualities.For(type, subtype).Contains(MatchedQualityKey) ? null : MatchedQualityKey;
            var changed = new FileRow(current.Choice.Path, true, season, episode, target, quality, string.Join(',', MatchedAudioLanguages ?? []), isBook ? "" : string.Join(',', MatchedSubtitleLanguages ?? []));
            ImportFiles = [.. _lines.Select((line, position) => position == index ? changed : ToRow(line.Choice))];
            _lines = await BuildLinesAsync(ct);
            _matching = null;
        }

        private static FileRow ToRow(ManualImportChoiceV1 choice) => new(choice.Path, choice.Imports, choice.Season, choice.Episode, choice.WantedItemId, choice.QualityKey,
            string.Join(',', choice.AudioLanguages), string.Join(',', choice.SubtitleLanguages));

        // Every chosen file goes at once; a file without its title or an episode without its numbers is first asked for.
        private async Task<IActionResult> RunAsync(bool finishes, CancellationToken ct)
        {
            if (_lines.Any(line => line.Choice.Imports && (line.WantedItemId is null || line.MediaType == MediaType.Series && (line.Choice.Season is null || line.Choice.Episode is null))))
            {
                _notice = "manualImport.incomplete";
                PreparePage();
                return Page();
            }

            var choices = new BaseDataList<ManualImportChoiceV1>(_lines.Select(line => line.Choice));
            _result = ImportOperationId is { } operationId
                ? await runDownload.ExecuteAsync<ManualImportResultV1>(new ActivityOperationParametersV1(operationId), new RunManualImportDownloadDataV1(choices, finishes), ct)
                : await run.ExecuteAsync<ManualImportResultV1>(new ManualImportFolderParametersV1(), new RunManualImportDataV1(StorageMountId!.Value, RelativePath ?? "", choices), ct);
            Hint("manualImport.done", _result.Imported);
            PreparePage();
            return Page();
        }

        // A script imports one chosen file and puts its answer into the file's status cell: moved to where, or failed with why. Every failure is an answer, never an error page.
        public async Task<IActionResult> OnPostImportRunFileAsync()
        {
            var ct = HttpContext.RequestAborted;
            var row = (ImportFiles ?? []).FirstOrDefault(file => file.Path == RunPath);
            IUiComponent answer;
            var imported = false;
            try
            {
                if (row?.Path is not { } path || row.WantedItemId is null) throw new DataValidationException(nameof(ManualImportChoiceV1.WantedItemId), "required");
                var choice = new ManualImportChoiceV1(path, true, row.Season, row.Episode, row.WantedItemId, BlankToNull(row.QualityKey), Codes(row.AudioLanguages), Codes(row.SubtitleLanguages));
                var choices = new BaseDataList<ManualImportChoiceV1>([choice]);
                var result = ImportOperationId is { } operationId
                    ? await runDownload.ExecuteAsync<ManualImportResultV1>(new ActivityOperationParametersV1(operationId), new RunManualImportDownloadDataV1(choices), ct)
                    : await run.ExecuteAsync<ManualImportResultV1>(new ManualImportFolderParametersV1(), new RunManualImportDataV1(StorageMountId ?? 0, RelativePath ?? "", choices), ct);
                var outcome = result.Files.FirstOrDefault(file => file.Path == path) ?? new ManualImportOutcomeV1(path, "", "import_assignment_failed");
                imported = outcome.FailureKey is null;
                answer = new Div(OutcomeChip(outcome), new Span(new Truncated(OutcomeText(outcome))).Class("manual-import__outcome"));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var error = ServiceError.Describe(exception, HttpContext, logger);
                answer = new Div(new StateChip(new Text("manualImport.state.failed"), UiTone.Danger, Glyph("alert", 14)),
                    new Span(new Truncated(texts["manualImport.failedPrefix"] + " " + texts[error.FieldResourceKey ?? error.ResourceKey] + (error.Detail is { } detail ? " " + detail : ""))).Class("manual-import__outcome"));
            }

            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            new Div(answer).Attribute("data-manual-outcome", imported ? "imported" : "failed").WriteTo(writer, HtmlEncoder.Default, texts);
            Response.Headers.CacheControl = "no-store";
            return Content(writer.ToString(), "text/html");
        }

        public async Task<IActionResult> OnGetImportFoldersAsync(long? storageMountId, string? path, string? afterName, int mountPage = 1)
        {
            var content = await StorageBrowser.FragmentAsync(ImportAddress("ImportFolders"), storageMountId, path, afterName, mountPage, false, mounts, mount, directories, localPaths, texts, HttpContext.RequestAborted);
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            content.WriteTo(writer, HtmlEncoder.Default, texts);
            Response.Headers.CacheControl = "no-store";
            return Content(writer.ToString(), "text/html");
        }

        // What the dialog shows: the waiting download's files, or the chosen folder's.
        private async Task LoadImportAsync()
        {
            if (!IsImporting || !texts.Can("acquisition.manage", "Acquisition")) return;
            var ct = HttpContext.RequestAborted;
            try
            {
                await ReadImportAsync(ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A folder that cannot be read says why inside the dialog; the queue behind it stays.
                _readError = ServiceError.Describe(exception, HttpContext, logger);
                _folderRead = false;
                _lines = [];
            }
        }

        private async Task ReadImportAsync(CancellationToken ct)
        {
            if (ImportOperationId is { } operationId)
            {
                var download = await readDownload.ExecuteAsync<ManualImportDownloadV1>(new ActivityOperationParametersV1(operationId), default, ct);
                _files = download.Files;
                _folder = download.Folder;
                _isReadOnly = download.IsReadOnly;
                _folderRead = true;
            }
            else if (StorageMountId is { } mountId && mountId > 0)
            {
                _mountName = (await mount.ExecuteAsync<StorageMountDetailsV1>(new StorageMountParametersV1(mountId), ct)).Name;
                if (RelativePath is { } path)
                {
                    var folder = await readFiles.ExecuteAsync<ManualImportFolderV1>(new ManualImportFolderParametersV1(), new ReadManualImportDataV1(mountId, path), ct);
                    _files = folder.Files;
                    _isReadOnly = folder.IsReadOnly;
                    _folderRead = true;
                }
            }

            if (_folderRead) _lines = await BuildLinesAsync(ct);
        }

        // Every file with its choice: what a person set before, else what was recognised; the names follow the choices through the library's own rules.
        private async Task<List<FileLine>> BuildLinesAsync(CancellationToken ct)
        {
            var posted = (ImportFiles ?? []).Where(row => row.Path is not null).GroupBy(row => row.Path!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            // A file that fits is chosen at once; one that wants a look, or was imported already, is not.
            var choices = _files.Select(file => posted.TryGetValue(file.Path, out var row) ? new ManualImportChoiceV1(file.Path, row.Imports, row.Season, row.Episode, row.WantedItemId, BlankToNull(row.QualityKey),
                    Codes(row.AudioLanguages), Codes(row.SubtitleLanguages))
                : new ManualImportChoiceV1(file.Path, !file.IsHandled && file.ProblemKey is null && file.UnplacedReason is null && file.WantedItemId is not null, file.Season, file.Episode, file.WantedItemId)).ToList();

            // A choice a person changed is named again by the library's rules.
            var changed = _files.Zip(choices).Where(pair => pair.Second.WantedItemId is not null && (pair.Second.WantedItemId != pair.First.WantedItemId || pair.Second.Season != pair.First.Season
                || pair.Second.Episode != pair.First.Episode || pair.Second.QualityKey is not null)).Select(pair => pair.Second).ToArray();
            Dictionary<string, ManualImportNameV1> names = [];
            if (changed.Length > 0)
            {
                var named = await preview.ExecuteAsync<BaseDataList<ManualImportNameV1>>(new ManualImportFolderParametersV1(), new PreviewManualImportDataV1(new BaseDataList<ManualImportChoiceV1>(changed)), ct);
                names = named.ToDictionary(name => name.Path, StringComparer.Ordinal);
            }

            return [.. _files.Zip(choices, (file, choice) => names.TryGetValue(file.Path, out var name)
                ? new FileLine(file, choice, name.WantedItemId, name.WorkTitle, name.MediaTypeId, name.TargetName, name.MediaSubtypeId, ProblemOf(name.MediaTypeId, choice, name.TargetName))
                : new FileLine(file, choice, file.WantedItemId, file.WorkTitle, file.MediaTypeId, file.TargetName, file.MediaSubtypeId, file.ProblemKey))];
        }

        // What a person still must settle of a file they changed: an episode's numbers, a book file the library would not place.
        private static string? ProblemOf(MediaType type, ManualImportChoiceV1 choice, string? targetName) => type switch
        {
            MediaType.Series when choice.Season is null || choice.Episode is null => "episode_unknown",
            MediaType.Book when targetName is null => "volume_unknown",
            _ => null
        };

        private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        // The codes of a hidden field, as the language catalog knows them.
        private static string[] Codes(string? value) => [.. (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(code => FileLanguages.Known.Any(known => known.Three == code))];

        // The queue's address with the dialog open: a handler of it, a folder or a waiting download; the queue's own choices stay.
        private string ImportAddress(string? handler = null, long? storageMountId = null, string? relativePath = null, long? operationId = null)
        {
            var more = new List<string> { "import=true" };
            if (handler is not null) more.Add("handler=" + handler);
            if ((storageMountId ?? (handler is null ? null : StorageMountId)) is { } mount) more.Add("storageMountId=" + mount.ToString(CultureInfo.InvariantCulture));
            if ((relativePath ?? (handler is "ImportRunFile" ? RelativePath : null)) is { } path) more.Add("relativePath=" + Uri.EscapeDataString(path));
            if ((operationId ?? (handler is null ? null : ImportOperationId)) is { } operation) more.Add("importOperation=" + operation.ToString(CultureInfo.InvariantCulture));
            return Views.Address("/Admin/Queue", null, [.. more]);
        }

        // What the form sends of one file.
        public sealed record FileRow(string? Path, bool Imports, int? Season, int? Episode, long? WantedItemId, string? QualityKey, string? AudioLanguages = null, string? SubtitleLanguages = null);

        // A file with what a person chose for it, the film, series or book that choice leads to (none yet when nothing fits) and what still wants a look.
        private sealed record FileLine(ManualImportFileV1 File, ManualImportChoiceV1 Choice, long? WantedItemId, string? WorkTitle, MediaType MediaType, string? TargetName, MediaSubtype? MediaSubtype,
            string? Problem);
    }
}
