using System.Globalization;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Wanted;

namespace Jularr.Frontend.Pages.Admin.Work
{
    public sealed partial class WorkModel
    {
        // Columns of the unit table in their default order; the ones after "quality" start hidden.
        private static readonly string[] UnitColumns = ["monitored", "number", "title", "aired", "languages", "group", "size", "status", "quality", "languagesIso", "languagesNames", "path", "codec", "range"];
        private static readonly string[] FileColumns = ["title", "languages", "group", "size", "quality", "languagesIso", "languagesNames", "path", "codec", "range"];
        private static readonly HashSet<string> HiddenByDefault = ["languagesIso", "languagesNames", "path", "codec", "range"];
        private static readonly string[] SortableColumns = ["number", "title", "aired", "size", "status", "quality", "group"];

        private void DefineManagement(Page page, WorkManagementV1 work)
        {
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            page.Title("admin.works.title", work.CanonicalTitle);
            page.Parent("admin.works.title", "/Admin/Works");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Work/Work.entry.scss");
            page.Asset("Pages/Admin/Work/Work.entry.ts");
            page.Asset("Framework/Components/Form.entry.ts");
            var isSeries = work.MediaTypeId == MediaType.Series;

            page.Content.Add(Hero(work, csrf));
            page.Content.Add(new NavigationTabs([new(isSeries ? "work.tab.seasons" : "work.tab.files", Address(), Tab == WorkTab.Units), new("work.tab.history", Address(WorkTab.History), Tab == WorkTab.History),
                new("work.tab.metadata", Address(WorkTab.Metadata), Tab == WorkTab.Metadata)], "work.tabs"));
            if (FormError is not null && _choice is null && Tab == WorkTab.Units) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
            switch (Tab)
            {
                case WorkTab.History:
                    page.Content.AddRange(History(work, csrf));
                    break;
                case WorkTab.Metadata:
                    DefineMetadata(page, standalone: false);
                    break;
                default:
                    page.Content.Add(isSeries ? Seasons(work, csrf) : Files(work));
                    break;
            }

            if (_wanted is not null && CanAcquire && _profiles.Count > 0) page.Content.Add(EditDialog(work, csrf));
        }

        // The cover on its banner, the title with its facts and figures, and the actions of an administrator.
        private IUiComponent Hero(WorkManagementV1 work, string? csrf)
        {
            var banner = work.BackdropUrl ?? work.PosterUrl;
            var cover = work.PosterUrl is { } poster ? new MediaImage(poster, "", 210, 315, priority: true) : (IUiComponent)new Div().Class("work-cover__placeholder");
            var pills = new List<IUiComponent>();
            if (work.ReleaseYear is { } year) pills.Add(new Span(new ValueText(year.ToString(CultureInfo.InvariantCulture))).Class("work-pill"));
            pills.Add(new Span(new Text(MediaTypeLabel.Key(work.MediaTypeId, work.MediaSubtypeId))).Class("work-pill"));

            var facts = string.Join(" · ", new[] { work.Genres, work.RuntimeMinutes is { } minutes ? string.Format(CultureInfo.CurrentCulture, texts["details.minutes"], minutes) : null, work.Studio }.Where(text => !string.IsNullOrWhiteSpace(text)));
            var figures = new List<IUiComponent>();
            void Figure(string key, string oneKey, long value, string? tone = null)
            {
                if (figures.Count > 0) figures.Add(new Span(new ValueText("·")).Class("work-figures__separator"));
                figures.Add(new Span(new ValueText(string.Format(CultureInfo.CurrentCulture, texts[value == 1 ? oneKey : key], value.ToString(CultureInfo.CurrentCulture)))).Class("work-figures__item" + (tone is null ? "" : " work-figures__item--" + tone)));
            }

            if (work.MediaTypeId == MediaType.Series)
            {
                Figure("work.figure.seasons", "work.figure.seasonOne", work.SeasonCount);
                Figure("work.figure.episodes", "work.figure.episodeOne", work.EpisodeCount);
            }

            if (work.OnDiskBytes > 0 || work.Files.Count > 0) figures.Add(new Span(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["work.figure.onDisk"], ByteSize.Format(work.OnDiskBytes)))).Class("work-figures__item"));
            if (work.MissingCount > 0) Figure("work.figure.missing", "work.figure.missing", work.MissingCount, "bad");
            if (work.UpgradeWantedCount > 0) Figure("work.figure.upgrades", "work.figure.upgradeOne", work.UpgradeWantedCount, "warn");
            if (work.UnairedCount > 0) Figure("work.figure.unaired", "work.figure.unaired", work.UnairedCount);

            var actions = new List<IUiComponent>();
            if (_wanted is { } wanted && CanAcquire)
            {
                actions.Add(new Form<UpdateWantedDataV1>(form =>
                {
                    form.Hidden(data => data.IsPaused);
                    form.Content(new SwitchButton(!wanted.IsPaused, wanted.IsPaused ? "work.monitor.start" : "work.monitor.stop", "work.monitored"));
                }, Address(Tab, "Monitor"), csrf, new UpdateWantedDataV1(!wanted.IsPaused, wanted.Version, wanted.AcquisitionProfileId, wanted.LibraryRootId)));
                actions.Add(new Form<SearchWantedDataV1>(form =>
                {
                    form.Hidden(data => data.ParentUnitNumber);
                    form.Hidden(data => data.UnitNumber);
                    form.Submit(Labeled("search", work.MediaTypeId == MediaType.Series ? "work.searchMonitored" : "work.search").Primary());
                }, Address(Tab, "Automatic"), csrf, new SearchWantedDataV1()));
                actions.Add(new A(Icon("user"), new Text("work.interactive")).Class("button").Attribute("href", WantedStatus.Home(wanted.Id) + "/Search"));
                actions.Add(new Form<WorkParametersV1>(form => form.Submit(Labeled("sync", "work.refresh")), Address(Tab, "Refresh"), csrf, new WorkParametersV1(work.Id)));
                actions.Add(new Button(Icon("pencil"), new Text("work.edit")).Attribute("data-dialog-open", "work-edit").Attribute("type", "button"));
            }

            var status = new List<IUiComponent>();
            if (TempData["WantedSearched"] is string outcome)
            {
                status.Add(new P(new Text("acquisition.outcome." + outcome), new ValueText(TempData["WantedTaken"] is string taken ? ": " + taken : "")).Attribute("role", "status").Class("work-hero__status"));
            }

            return new Section(
                banner is null ? new Div() : (IUiComponent)new Div(new MediaImage(banner, "", 1280, 360)).Class("work-banner" + (work.BackdropUrl is null ? " work-banner--cover" : "")),
                new Div(
                    new Div(cover).Class("work-cover"),
                    new Div(
                        new Div(new H1(new ValueText(work.CanonicalTitle)), new Div([.. pills]).Class("work-pills")).Class("work-title"),
                        facts.Length == 0 ? new Span() : new P(new ValueText(facts)).Class("work-facts"),
                        string.IsNullOrWhiteSpace(work.Overview) ? new Span() : new P(new ValueText(work.Overview)).Class("work-overview"),
                        new Div([.. status, new Div([.. figures]).Class("work-figures"), new Div([.. actions]).Class("work-actions")]).Class("work-hero__bottom")).Class("work-hero__body")).Class("work-hero__content")).Class("work-hero");
        }

        // Seasons newest first as accordions: the unit table of a season opens with its round button.
        private IUiComponent Seasons(WorkManagementV1 work, string? csrf)
        {
            var seasons = work.Units.Where(unit => unit.WorkUnitTypeId == WorkUnitType.Season).ToArray();
            if (seasons.Length == 0) return new P(new Text("work.empty")).Attribute("role", "status");
            var episodesOf = work.Units.Where(unit => unit.WorkUnitTypeId == WorkUnitType.Episode && unit.ParentWorkUnitId is not null).ToLookup(unit => unit.ParentWorkUnitId!.Value);
            var seasonNumbers = seasons.ToDictionary(season => season.Id, season => season.Number);
            var content = new List<IUiComponent> { new Div(new Button(Icon("chevron-down", 22), new Text("work.collapseAll")).Attribute("data-collapse-all", "").Attribute("type", "button")).Class("work-seasons__tools") };
            var first = true;
            foreach (var season in seasons)
            {
                var episodes = episodesOf[season.Id].ToArray();
                var panelId = "work-season-" + season.Id.ToString(CultureInfo.InvariantCulture);
                var counted = episodes.Where(episode => episode.WorkUnitStatusId != WorkUnitStatus.Unaired).ToArray();
                var present = counted.Count(episode => episode.WorkUnitStatusId == WorkUnitStatus.Present);
                var missing = episodes.Count(episode => episode.WorkUnitStatusId == WorkUnitStatus.Missing);
                var unaired = episodes.Length - counted.Length;
                var bytes = work.Files.Where(file => !file.IsMissing && file.WorkUnitIds.Any(id => episodes.Any(episode => episode.Id == id))).Sum(file => file.SizeBytes);
                var name = season.Number == 0 ? texts["work.specials"] : MediaUnitLabel.Format(texts, WorkUnitType.Season, season.Number, null)!;
                var info = new List<string> { string.Format(CultureInfo.CurrentCulture, texts["work.coverage"], present, counted.Length) };
                if (missing > 0) info.Add(string.Format(CultureInfo.CurrentCulture, texts["work.figure.missing"], missing));
                if (unaired > 0) info.Add(string.Format(CultureInfo.CurrentCulture, texts["work.figure.unaired"], unaired));
                if (bytes > 0) info.Add(ByteSize.Format(bytes));
                var open = first;
                first = false;

                content.Add(new Div(
                    new Div(
                        new Div(UnitSwitch(season, work, csrf), new H2(new ValueText(name)),
                            new Div(new Progress().Attribute("value", present.ToString(CultureInfo.InvariantCulture)).Attribute("max", Math.Max(1, counted.Length).ToString(CultureInfo.InvariantCulture))
                                .Class("work-coverage__bar" + (missing > 0 ? " work-coverage__bar--partial" : "")), new Span(new ValueText(string.Join(" · ", info)))).Class("work-coverage")).Class("work-season__lead"),
                        new Button(Icon("chevron-down", 22)).Class("work-season__toggle").Attribute("type", "button").Attribute("data-season-toggle", panelId).Attribute("aria-expanded", open ? "true" : "false")
                            .Attribute("aria-controls", panelId).TextAttribute("aria-label", "work.toggleSeason"),
                        new Div(SeasonActions(season, work, seasonNumbers, csrf)).Class("work-season__actions")).Class("work-season__head"),
                    new Div(UnitTable(episodes, work, seasonNumbers, csrf)).Class("work-season__panel").Attribute("id", panelId).Attribute("data-open", open ? "true" : "false")).Class("work-season"));
            }

            content.Add(ColumnPicker());
            return new Div([.. content]).Class("work-seasons");
        }

        // A film's files, each as its own row.
        private IUiComponent Files(WorkManagementV1 work)
        {
            if (work.Files.Count == 0) return new P(new Text("work.empty.files")).Attribute("role", "status");
            return new Div(FileTable(work), ColumnPicker()).Class("work-files");
        }

        private IUiComponent UnitTable(WorkManagementUnitV1[] episodes, WorkManagementV1 work, Dictionary<long, decimal> seasonNumbers, string? csrf)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var rows = new List<IUiComponent>();
            foreach (var episode in episodes)
            {
                var files = work.Files.Where(file => !file.IsMissing && file.WorkUnitIds.Contains(episode.Id)).OrderBy(file => (int)Rank(file.FileQualityGradeId)).ThenByDescending(file => file.SizeBytes).ToArray();
                var best = files.FirstOrDefault();
                var cells = new Dictionary<string, Td>
                {
                    ["monitored"] = Cell("monitored", UnitSwitch(episode, work, csrf)),
                    ["number"] = Cell("number", new ValueText(episode.Number.ToString("0.###", CultureInfo.InvariantCulture)), episode.Number.ToString("000000.000", CultureInfo.InvariantCulture)),
                    ["title"] = Cell("title", new Span(new ValueText(episode.Title ?? "")).Class("work-title-text"), episode.Title ?? "", files.Length > 1 ? VersionsButton(episode.Id, files.Length) : null),
                    ["aired"] = Cell("aired", new ValueText(episode.ReleasedOn?.ToString("d MMM yyyy", CultureInfo.CurrentCulture) ?? ""), episode.ReleasedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""),
                    ["status"] = Cell("status", StatusPill(episode, today), ((int)episode.WorkUnitStatusId).ToString(CultureInfo.InvariantCulture))
                };
                foreach (var (key, cell) in FileCells(files.Length > 1 ? null : best, files.Length > 1 ? files.Sum(file => file.SizeBytes) : null, files.Length > 1 ? files[0] : null)) cells[key] = cell;
                if (_wanted is not null && CanAcquire)
                {
                    var actionsCell = new Td(new Div(SearchIcon(episode, seasonNumbers, csrf, "work.search.episode", true), SearchIcon(episode, seasonNumbers, csrf, "work.interactive.episode", false)).Class("work-icons"));
                    actionsCell.Attribute("data-col", "actions");
                    cells["actions"] = actionsCell;
                }

                rows.Add(new Tbody([RowOf(UnitColumns, cells), .. files.Length > 1 ? files.Select(file => FileRow(file, episode.Id, UnitColumns)) : []]).Class("work-unit"));
            }

            return Table("units", UnitColumns, rows);
        }

        private IUiComponent FileTable(WorkManagementV1 work)
        {
            var rows = work.Files.Where(file => !file.IsMissing).OrderBy(file => (int)Rank(file.FileQualityGradeId)).ThenByDescending(file => file.SizeBytes)
                .Select(file => (IUiComponent)new Tbody(FileRow(file, null, FileColumns)).Class("work-unit")).ToList();
            return Table("files", FileColumns, rows);
        }

        // A file in the columns of its table, for a film or below the unit that has several.
        private IUiComponent FileRow(WorkManagementFileV1 file, long? unitId, string[] columns)
        {
            var cells = new Dictionary<string, Td> { ["title"] = Cell("title", new Span(new ValueText(Path.GetFileName(file.RelativePath))).Class("work-title-text"), Path.GetFileName(file.RelativePath)) };
            foreach (var (key, cell) in FileCells(file, null, file)) cells[key] = cell;
            var row = RowOf(columns, cells).Class("work-file");
            if (unitId is { } id) row.Attribute("data-unit", id.ToString(CultureInfo.InvariantCulture)).Attribute("hidden", "hidden");
            return row;
        }

        // Quality, size and languages of one file; several versions show their total size and the best one's quality.
        private Dictionary<string, Td> FileCells(WorkManagementFileV1? file, long? totalBytes, WorkManagementFileV1? best)
        {
            var cells = new Dictionary<string, Td>();
            var shown = file ?? best;
            if (shown is null) return cells;
            cells["quality"] = Cell("quality", QualityBadge(shown), ((int)Rank(shown.FileQualityGradeId)).ToString(CultureInfo.InvariantCulture));
            cells["size"] = Cell("size", SizeBlock(shown, totalBytes), (totalBytes ?? shown.SizeBytes).ToString("D20", CultureInfo.InvariantCulture));
            cells["group"] = Cell("group", new ValueText(shown.ReleaseGroup ?? ""), shown.ReleaseGroup ?? "");
            cells["languages"] = Cell("languages", LanguageGroup(shown, WorkLanguages.Mode.Flags));
            cells["languagesIso"] = Cell("languagesIso", LanguageGroup(shown, WorkLanguages.Mode.Iso));
            cells["languagesNames"] = Cell("languagesNames", LanguageGroup(shown, WorkLanguages.Mode.Names));
            cells["path"] = Cell("path", new ValueText(shown.RelativePath));
            cells["codec"] = Cell("codec", new ValueText(string.Join(" ", new[] { shown.VideoCodec, shown.Height is { } height ? height.ToString(CultureInfo.InvariantCulture) + "p" : null }.Where(text => text is not null))));
            cells["range"] = Cell("range", new ValueText(shown.DynamicRange ?? ""));
            return cells;
        }

        // Quality: green when the cutoff is met, amber with an arrow while an upgrade is wanted, red when the profile does not allow it.
        private IUiComponent QualityBadge(WorkManagementFileV1 file)
        {
            if (file.QualityKey is null) return new Span();
            var tone = file.FileQualityGradeId switch { FileQualityGrade.Met => "met", FileQualityGrade.Upgrade => "upgrade", FileQualityGrade.NotAllowed => "bad", _ => "unknown" };
            return new Span(new Span(new ValueText(file.QualityKey + (file.FileQualityGradeId == FileQualityGrade.Upgrade ? " ↑" : ""))).Class("work-quality work-quality--" + tone),
                file.FormatScore is { } score && score != 0 ? new Span(new ValueText("CF " + (score > 0 ? "+" : "") + score.ToString(CultureInfo.InvariantCulture))).Class("work-score") : new Span()).Class("work-quality-block");
        }

        // The size with a bar for where it lies in the profile's range, and what that comes to per minute.
        private IUiComponent SizeBlock(WorkManagementFileV1 file, long? totalBytes)
        {
            var tone = file.FileSizeGradeId switch { FileSizeGrade.Good => "good", FileSizeGrade.Small => "small", FileSizeGrade.Large => "large", _ => "unknown" };
            var detail = string.Join(" · ", new[] { file.MegabytesPerMinute is { } perMinute ? perMinute.ToString("0.#", CultureInfo.CurrentCulture) + " MB/min" : null, file.VideoCodec }.Where(text => text is not null));
            return new Div(new Span(new ValueText(ByteSize.Format(totalBytes ?? file.SizeBytes))).Class("work-size__value"),
                file.FileSizeGradeId == FileSizeGrade.Unknown ? new Span() : new Span(new Span().Class("work-size__fill")).Class("work-size__bar work-size__bar--" + tone),
                detail.Length == 0 ? new Span() : new Span(new ValueText(detail)).Class("work-size__detail")).Class("work-size");
        }

        private IUiComponent LanguageGroup(WorkManagementFileV1 file, WorkLanguages.Mode mode) =>
            new Span(Languages(file.AudioLanguages, mode), new Span(new ValueText("·")).Class("work-languages__separator"), Languages(file.SubtitleLanguages, mode)).Class("work-languages");

        private IUiComponent Languages(string[] codes, WorkLanguages.Mode mode) => new Span([.. codes.Select(WorkLanguages.Code).Distinct().Select(code => WorkLanguages.Render(code, mode, assets))]).Class("work-languages__group");

        private static FileQualityGrade Rank(FileQualityGrade grade) => grade switch { FileQualityGrade.Met => (FileQualityGrade)0, FileQualityGrade.Upgrade => (FileQualityGrade)1, FileQualityGrade.Unknown => (FileQualityGrade)2, _ => (FileQualityGrade)3 };

        private IUiComponent StatusPill(WorkManagementUnitV1 unit, DateOnly today)
        {
            var (key, tone) = unit.WorkUnitStatusId switch
            {
                WorkUnitStatus.Present => ("work.status.Present", "ok"),
                WorkUnitStatus.Missing => ("work.status.Missing", "bad"),
                WorkUnitStatus.Downloading => ("work.status.Downloading", "wait"),
                WorkUnitStatus.NotMonitored => ("work.status.NotMonitored", "off"),
                _ => ("work.status.Unaired", "off")
            };
            if (unit.WorkUnitStatusId == WorkUnitStatus.Unaired)
            {
                if (unit.ReleasedOn is null) return new Span(new Text("work.status.tba")).Class("work-status work-status--off");
                var days = unit.ReleasedOn.Value.DayNumber - today.DayNumber;
                if (days is >= 1 and <= 14) return new Span(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["work.status.airsIn"], days))).Class("work-status work-status--off");
            }

            return new Span(new Text(key)).Class("work-status work-status--" + tone);
        }

        // Search of one unit by the same scope as before: a season, an episode of its season.
        private IUiComponent SeasonActions(WorkManagementUnitV1 season, WorkManagementV1 work, Dictionary<long, decimal> seasonNumbers, string? csrf) =>
            _wanted is null || !CanAcquire ? new Span() : new Div(SearchIcon(season, seasonNumbers, csrf, "work.search.season", true), SearchIcon(season, seasonNumbers, csrf, "work.interactive.season", false)).Class("work-icons");

        private IUiComponent SearchIcon(WorkManagementUnitV1 unit, Dictionary<long, decimal> seasonNumbers, string? csrf, string labelKey, bool automaticSearch)
        {
            static int? Whole(decimal number) => number == decimal.Truncate(number) && number is >= 0 and <= int.MaxValue ? (int)number : null;
            var scope = unit.WorkUnitTypeId == WorkUnitType.Season
                ? Whole(unit.Number) is { } season ? new SearchWantedDataV1(season) : null
                : unit.ParentWorkUnitId is { } parent && seasonNumbers.TryGetValue(parent, out var number) && Whole(number) is { } seasonNumber && Whole(unit.Number) is { } episode ? new SearchWantedDataV1(seasonNumber, episode) : null;
            if (scope is null || _wanted is null) return new Span();
            return new Form<SearchWantedDataV1>(form =>
            {
                form.Hidden(data => data.ParentUnitNumber);
                form.Hidden(data => data.UnitNumber);
                form.Content(IconSubmit(automaticSearch ? "search" : "user", labelKey));
            }, automaticSearch ? Address(Tab, "Automatic") : WantedStatus.Home(_wanted.Id) + "/Search?handler=Search", csrf, scope);
        }

        private IUiComponent UnitSwitch(WorkManagementUnitV1 unit, WorkManagementV1 work, string? csrf)
        {
            if (_wanted is null || !CanAcquire) return new Span(new Text(unit.IsMonitored ? "work.monitor.on" : "work.monitor.off")).Class("work-switch work-switch--static" + (unit.IsMonitored ? " work-switch--on" : ""));
            return new Form<UpdateWantedUnitDataV1>(form =>
            {
                form.Hidden(data => data.IsMonitored);
                form.Content(new SwitchButton(unit.IsMonitored, unit.IsMonitored ? "work.monitor.stop" : "work.monitor.start"));
            }, Address(Tab, "Unit") + "&workUnitId=" + unit.Id.ToString(CultureInfo.InvariantCulture), csrf, new UpdateWantedUnitDataV1(!unit.IsMonitored));
        }

        // Where every episode's actions are: the whole unit table has them in its last column, and the gear of the column picker above.
        private IUiComponent Table(string kind, string[] columns, List<IUiComponent> bodies)
        {
            var head = new Tr([.. columns.Select(column => (IUiComponent)new Th(SortableColumns.Contains(column) ? (IUiComponent)new Button(new Text("work.col." + column)).Attribute("data-sort", column).Attribute("type", "button").Class("work-sort")
                    : new Text("work.col." + column)).Attribute("data-col", column).Attribute("data-label", texts["work.col." + column]).Attribute("scope", "col")
                .Attribute("data-hidden", HiddenByDefault.Contains(column) ? "true" : "false")),
                new Th(new Button(Icon("settings"), new Span(new Text("work.columns")).Class("visually-hidden")).Attribute("data-columns-open", "").Attribute("type", "button").Class("work-gear")).Attribute("data-col", "actions")]);
            return new Div(new TableElement([new Thead(head), .. bodies]).Class("work-table").Attribute("data-work-table", kind)).Class("work-table-wrap");
        }

        private Tr RowOf(string[] columns, Dictionary<string, Td> cells)
        {
            var row = new Tr([.. columns.Select(column => (IUiComponent)(cells.TryGetValue(column, out var cell) ? cell : Cell(column, new Span()))), RowActions(cells)]);
            row.Class("work-row");
            return row;
        }

        // The actions of a unit row are attached after the data cells; a file row has none yet.
        private static IUiComponent RowActions(Dictionary<string, Td> cells) => cells.TryGetValue("actions", out var actions) ? actions : new Td(new Span()).Attribute("data-col", "actions");

        private static Td Cell(string column, IUiComponent content, string? sort = null, IUiComponent? extra = null)
        {
            var cell = new Td(extra is null ? [content] : [content, extra]);
            cell.Attribute("data-col", column);
            if (sort is not null) cell.Attribute("data-value", sort);
            return cell;
        }

        private IUiComponent VersionsButton(long unitId, int count) => new Button(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["work.versions"], count))).Class("work-versions")
            .Attribute("data-versions-toggle", unitId.ToString(CultureInfo.InvariantCulture)).Attribute("type", "button").Attribute("aria-expanded", "false");

        // The columns a person may show, in the order of the table; filled in from the table's headers by the client.
        private IUiComponent ColumnPicker() => new Div(new H3(new Text("work.columns")), new Ul().Class("work-columns__list")).Class("work-columns").Attribute("data-column-picker", "").Attribute("data-move-label", texts["work.columns.move"]).Attribute("hidden", "hidden");

        // Releases of the title and what happened to it.
        private List<IUiComponent> History(WorkManagementV1 work, string? csrf)
        {
            var content = new List<IUiComponent>();
            if (_events is { } events)
            {
                content.Add(new Table<ActivityEventV1>(events.Items)
                    .Text(item => item.OccurredAt, "history.when")
                    .Resource(item => item.ActivityEventTypeId, "history.event", type => "history.event." + type)
                    .Content("table.work", item => new ValueText(MediaUnitLabel.Format(texts, item.WorkUnitTypeId, item.UnitNumber, item.ParentUnitNumber) ?? ""))
                    .Content("history.release", item => new Div(new ValueText(item.ReleaseTitle ?? ""), new Span(new ValueText(item.QualityKey ?? ""))))
                    .Content("history.source", item => new ValueText(string.Join(" · ", new[] { item.IndexerName, item.ClientName }.OfType<string>())))
                    .Content("history.file", item => new Div(item.PreviousPath is { } before ? new Span(new ValueText(before + " → ")) : new Span(), new ValueText(item.Path ?? ""),
                        item.ReasonKey is { } reason ? new Span(new Text("acquisition.outcome." + reason)) : new Span()))
                    .Content("history.by", item => new ValueText(item.AccountName ?? texts["history.byJularr"])));
                if (events.Items.Count == 0) content.Add(new P(new Text("history.empty")));
                if (events.Page > 1) content.Add(new A(new Text("common.previous")).Attribute("href", Address(WorkTab.History, null, events.Page - 1)));
                if (events.Items.Count == events.PageSize) content.Add(new A(new Text("common.next")).Attribute("href", Address(WorkTab.History, null, events.Page + 1)));
            }

            if (_releases.Count > 0)
            {
                content.Add(new UiSection("work.releases", [new Table<WantedReleaseV1>(_releases)
                    .Text(item => item.Title, "wanted.search.release")
                    .Text(item => item.ConnectionName, "wanted.search.indexer")
                    .Text(item => item.QualityKey, "wanted.search.quality")
                    .Resource(item => item.AcquisitionReleaseStatusTypeId, "table.status", status => "acquisition.release." + status)
                    .Content("wanted.search.reason", item => item.ReasonKey is { } reason ? new Text("acquisition.outcome." + reason) : new Span())
                    .Content("wanted.history.date", item => new ValueText(item.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)))
                    .Content("common.actions", item => item.AcquisitionReleaseStatusTypeId == Jularr.Backend.Data.Enums.Acquisition.AcquisitionReleaseStatusType.Grabbed || !CanAcquire
                        ? new Span()
                        : new Form<MarkWantedReleaseDataV1>(form =>
                        {
                            form.Hidden(data => data.AcquisitionReleaseStatusTypeId);
                            form.Submit(item.IsBlocked ? "wanted.release.unblock" : "wanted.release.block");
                        }, Address(WorkTab.History, "Mark") + "&acquisitionReleaseId=" + item.Id.ToString(CultureInfo.InvariantCulture), csrf,
                            new MarkWantedReleaseDataV1(item.IsBlocked ? Jularr.Backend.Data.Enums.Acquisition.AcquisitionReleaseStatusType.Allowed : Jularr.Backend.Data.Enums.Acquisition.AcquisitionReleaseStatusType.Blocked)))]));
            }

            return content;
        }

        // The profile, library and tags of the title and whether new episodes are monitored.
        private IUiComponent EditDialog(WorkManagementV1 work, string? csrf)
        {
            var wanted = _wanted!;
            var isOpen = _choice is not null && FormError is not null;
            var form = new Form<UpdateWantedDataV1>(form =>
            {
                form.Hidden(data => data.IsPaused);
                form.Hidden(data => data.Version);
                form.Select(data => data.AcquisitionProfileId, "wanted.profile", [new SelectOption("", texts["wanted.profile.default"]),
                    .. _profiles.Select(profile => new SelectOption(profile.Id.ToString(CultureInfo.InvariantCulture), profile.Name))]);
                form.Select(data => data.LibraryRootId, "wanted.library", [new SelectOption("", texts["wanted.library.default"]),
                    .. _libraries.Select(library => new SelectOption(library.Id.ToString(CultureInfo.InvariantCulture), library.Name))]);
                if (work.MediaTypeId == MediaType.Series)
                {
                    form.Select(data => data.MonitorsNewUnits, "wanted.monitorsNewUnits", [new SelectOption("True", texts["wanted.unit.monitored"]), new SelectOption("False", texts["wanted.unit.unmonitored"])]);
                }

                form.Choices(data => data.TagIds, "arr.tags", _tagChoices);
                form.Save();
            }, Address(Tab, "Choice"), csrf, _choice ?? new UpdateWantedDataV1(wanted.IsPaused, wanted.Version, wanted.AcquisitionProfileId, wanted.LibraryRootId, wanted.MonitorsNewUnits, tagIds: wanted.TagIds), _choice is null ? null : FormError);
            return new Dialog(new Div(new H2(new Text("work.edit")), new Button(new Text("folderPicker.close")).Attribute("data-dialog-close", "").Attribute("type", "button")).Class("work-dialog__head"), form)
                .Class("work-dialog").Attribute("id", "work-edit").Attribute("data-open", isOpen ? "true" : "false");
        }

        private Button Labeled(string icon, string labelKey) => new(Icon(icon), new Text(labelKey));

        private IUiComponent Icon(string name, int size = 20) => new Framework.Icon(name, assets, size, "2.4");

        private HtmlElement IconSubmit(string icon, string labelKey) => new IconButton(Icon(icon, 22)).Attribute("type", "submit").Class("work-icon").TextAttribute("aria-label", labelKey).TextAttribute("title", labelKey);

        private sealed class Td(params IUiComponent[] children) : HtmlElement("td", children);

        private sealed class Dialog(params IUiComponent[] children) : HtmlElement("dialog", children);

        private sealed class IconButton(params IUiComponent[] children) : HtmlElement("button", children);

        // A switch that submits its form: it shows the state and the click is the change.
        private sealed class SwitchButton : HtmlElement
        {
            public SwitchButton(bool isOn, string actionKey, string? labelKey = null) : base("button", [new Span().Class("work-switch__track"), labelKey is null ? new Span() : new Span(new Text(labelKey)).Class("work-switch__label")])
            {
                Attribute("type", "submit");
                Attribute("role", "switch");
                Attribute("aria-checked", isOn ? "true" : "false");
                TextAttribute("aria-label", actionKey);
                Class("work-switch" + (isOn ? " work-switch--on" : ""));
            }
        }
    }
}
