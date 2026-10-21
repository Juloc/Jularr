using System.Globalization;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
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
        private static readonly string[] VolumeColumns = ["monitored", "number", "title", "released", "format", "pages", "group", "size", "status", "quality", "path"];
        private static readonly string[] EditionColumns = ["title", "format", "group", "size", "quality", "path"];
        private static readonly string[] AudiobookColumns = ["title", "format", "codec", "length", "perhour", "group", "size", "quality", "path"];
        private static readonly string[] GameColumns = ["title", "format", "region", "version", "group", "size", "quality", "rom", "path"];
        private static readonly string[] TrackColumns = ["number", "title", "length", "format", "group", "size", "status", "quality", "path"];
        private static readonly string[] FileColumns = ["title", "languages", "group", "size", "quality", "languagesIso", "languagesNames", "path", "codec", "range"];
        private static readonly HashSet<string> HiddenByDefault = ["languagesIso", "languagesNames", "path", "codec", "range"];
        private static readonly string[] SortableColumns = ["perhour", "pages", "region", "version", "number", "absolute", "title", "aired", "released", "format", "length", "size", "status", "quality", "group", "rom"];

        // Anime counts episodes across seasons as well.
        private static string[] ColumnsOf(WorkManagementV1 work) => work.MediaTypeId == MediaType.Music ? TrackColumns : work.MediaTypeId == MediaType.Book ? VolumeColumns
            : work.MediaSubtypeId == MediaSubtype.Anime ? [.. UnitColumns.Take(2), "absolute", .. UnitColumns.Skip(2)] : UnitColumns;

        private void DefineManagement(Page page, WorkManagementV1 work)
        {
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            page.Title("admin.works.title", work.CanonicalTitle);
            page.Parent("admin.works.title", "/Admin/Works");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Work/Work.entry.scss");
            page.Asset("Pages/Admin/Work/Work.entry.ts");
            page.Asset("Framework/Components/LiveProgress.entry.ts");
            var isSeries = work.MediaTypeId == MediaType.Series;
            var hasVolumes = work.MediaTypeId == MediaType.Book && work.MediaSubtypeId is not null;
            var hasGroups = work.MediaTypeId == MediaType.Game || work.MediaTypeId == MediaType.Book && work.MediaSubtypeId is null;
            var hasTracks = work.MediaTypeId == MediaType.Music;
            var hasUnits = isSeries || hasVolumes || hasTracks;

            // Everything below the breadcrumb is one region: an action on this page (a switch, a search, a delete) swaps it and keeps the open sections.
            var regionStart = page.Content.Count;
            page.Content.Add(Hero(work, csrf));
            var tabs = new List<NavigationTab> { new(isSeries ? "work.tab.seasons" : hasVolumes ? "work.tab.volumes" : hasTracks ? "work.tab.tracks" : hasGroups ? work.MediaTypeId == MediaType.Game ? "work.tab.platforms" : "work.tab.editions" : "work.tab.files", Address(), Tab == WorkTab.Units), new("work.tab.history", Address(WorkTab.History), Tab == WorkTab.History) };
            if (hasUnits && texts.CanManageMedia()) tabs.Add(new("work.tab.manage", Address(WorkTab.Files), Tab == WorkTab.Files));
            if (isSeries && texts.CanEditMarkers()) tabs.Add(new("work.tab.markers", Home + "/Markers", false));
            tabs.Add(new("work.tab.metadata", Address(WorkTab.Metadata), Tab == WorkTab.Metadata));
            page.Content.Add(new NavigationTabs([.. tabs], "work.tabs"));
            if (FormError is not null && _choice is null && _deleteInput is null && Tab == WorkTab.Units) page.Content.Add(new P(ErrorText.Of(FormError)).Attribute("role", "alert"));
            switch (Tab)
            {
                case WorkTab.History:
                    page.Content.AddRange(History(work, csrf));
                    break;
                case WorkTab.Metadata:
                    DefineMetadata(page, standalone: false);
                    break;
                case WorkTab.Files when hasUnits && texts.CanManageMedia():
                    page.Content.Add(ManageFiles(work, csrf));
                    break;
                default:
                    page.Content.Add(isSeries ? Seasons(work, csrf) : hasVolumes || hasTracks ? FlatUnits(work, csrf) : hasGroups ? Groups(work, csrf) : Files(work, csrf));
                    break;
            }

            if (_wanted is not null && CanAcquire && _profiles.Count > 0 && work.MediaTypeId != MediaType.Game) page.Content.Add(EditDialog(work, csrf, _wanted, "work-edit"));
            if (work.MediaTypeId == MediaType.Game && _profiles.Count > 0) page.Content.AddRange(_platformTargets.Values.Select(target => EditDialog(work, csrf, target, "work-edit-" + target.Id.ToString(CultureInfo.InvariantCulture), true)));
            if (texts.CanManageMedia()) page.Content.Add(DeleteDialog(csrf));
            if (texts.CanManageMedia() && (hasUnits ? Tab == WorkTab.Files : Tab == WorkTab.Units) && work.Files.Any(file => !file.IsMissing)) page.Content.Add(EditFilesDialog(work, csrf));
            var region = page.Content.Skip(regionStart).ToArray();
            page.Content.RemoveRange(regionStart, page.Content.Count - regionStart);
            page.Content.Add(new Region("work-page", region));
        }

        // The cover on its banner, the title with its facts and figures, and the actions of an administrator.
        private IUiComponent Hero(WorkManagementV1 work, string? csrf)
        {
            var banner = work.BackdropUrl ?? work.PosterUrl;
            var cover = MediaImage.Or(assets, work.PosterUrl, "", 210, 315, priority: true);
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

            if (work.MediaTypeId == MediaType.Book) Figure("work.figure.volumes", "work.figure.volumeOne", work.Units.Count(unit => unit.WorkUnitTypeId == WorkUnitType.Volume));
            if (work.MediaTypeId == MediaType.Music) Figure("work.figure.tracks", "work.figure.trackOne", work.Units.Count(unit => unit.WorkUnitTypeId == WorkUnitType.Track));

            if (work.OnDiskBytes > 0 || work.Files.Count > 0) figures.Add(new Span(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["work.figure.onDisk"], ByteSize.Format(work.OnDiskBytes)))).Class("work-figures__item"));
            if (work.MissingCount > 0) Figure("work.figure.missing", "work.figure.missing", work.MissingCount, "bad");
            if (work.UpgradeWantedCount > 0) Figure("work.figure.upgrades", "work.figure.upgradeOne", work.UpgradeWantedCount, "warn");
            if (work.UnairedCount > 0) Figure("work.figure.unaired", "work.figure.unaired", work.UnairedCount);

            var actions = new List<IUiComponent>();
            if (_wanted is { } wanted && CanAcquire)
            {
                if (work.MediaTypeId != MediaType.Game) actions.Add(new Form<UpdateWantedDataV1>(form =>
                {
                    form.Hidden(data => data.IsPaused);
                    form.Content(new SwitchButton(!wanted.IsPaused, wanted.IsPaused ? "work.monitor.start" : "work.monitor.stop", "work.monitored"));
                }, Address(Tab, "Monitor"), csrf, new UpdateWantedDataV1(!wanted.IsPaused, wanted.Version, wanted.AcquisitionProfileId, wanted.LibraryRootId)));
                if (work.MediaTypeId != MediaType.Game) actions.Add(new Form<SearchWantedDataV1>(form =>
                {
                    form.Hidden(data => data.ParentUnitNumber);
                    form.Hidden(data => data.UnitNumber);
                    form.Submit(Labeled("search", work.MediaTypeId == MediaType.Series ? "work.searchMonitored" : "work.search").Primary());
                }, Address(Tab, "Automatic"), csrf, new SearchWantedDataV1()));
                if (work.MediaTypeId is not (MediaType.Series or MediaType.Game)) actions.Add(new A(Icon("user"), new Text("work.interactive")).Class("button").Attribute("href", WantedStatus.Home(wanted.Id) + "/Search"));
                actions.Add(new Form<WorkParametersV1>(form => form.Submit(Labeled("sync", "work.refresh")), Address(Tab, "Refresh"), csrf, new WorkParametersV1(work.Id)));
                if (work.MediaTypeId != MediaType.Game) actions.Add(new Button(Icon("pencil"), new Text("work.edit")).Attribute("data-dialog-open", "work-edit").Attribute("type", "button"));
            }

            // A manga volume from the administrator's own device.
            if (work.MediaSubtypeId == MediaSubtype.Manga && texts.CanUploadVolumes()) actions.Add(new A(Icon("document"), new Text("work.upload")).Class("button").Attribute("href", Home + "/Upload"));
            if (texts.CanManageMedia()) actions.Add(new Button(Icon("trash"), new Text("work.delete")).Attribute("data-dialog-open", "work-delete").Attribute("type", "button").Class("button work-delete"));

            var status = new List<IUiComponent>();

            // What its downloads do right now, shown only while there are some.
            if (CanAcquire && work.MediaTypeId != MediaType.Game && _wanted is { } target) status.Add(new LiveProgress(LiveKeys.Wanted(target.Id)).Details().Optional());
            if (CanAcquire && work.MediaTypeId == MediaType.Game) status.AddRange(_platformTargets.Values.Select(platform => new LiveProgress(LiveKeys.Wanted(platform.Id)).Details().Optional()));

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
            var content = new List<IUiComponent>
            {
                new Div(NumberingSwitch(work), new Button(Icon("chevron-down", 22), new Span(new Text("work.collapseAll")).Attribute("data-collapse-label", "")).Attribute("data-collapse-all", "").Attribute("type", "button")
                        .TextAttribute("data-collapse-text", "work.collapseAll").TextAttribute("data-expand-text", "work.expandAll"),
                    new Button(Icon("settings"), new Text("work.columns")).Attribute("data-columns-open", "").Attribute("data-columns-kind", "units").Attribute("type", "button").Class("button work-tools__columns")).Class("work-seasons__tools")
            };
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
        private IUiComponent Files(WorkManagementV1 work, string? csrf)
        {
            if (work.Files.Count == 0) return new P(new Text("work.empty.files")).Attribute("role", "status");
            return new Div(FileTable(work, csrf), ColumnPicker()).Class("work-files");
        }

        // The volumes of a manga or light novel, newest first, or the tracks of an album in their order, in one table; there are no seasons to open.
        private IUiComponent FlatUnits(WorkManagementV1 work, string? csrf)
        {
            var tracks = work.MediaTypeId == MediaType.Music;
            var type = tracks ? WorkUnitType.Track : WorkUnitType.Volume;
            var units = work.Units.Where(unit => unit.WorkUnitTypeId == type).OrderBy(unit => tracks ? unit.Number : -unit.Number).ToArray();
            if (units.Length == 0) return new P(new Text(tracks ? "work.empty.tracks" : "work.empty.volumes")).Attribute("role", "status");
            var kind = tracks ? "tracks" : "volumes";
            if (tracks && units.Select(unit => unit.DiscNumber).Distinct().Count() > 1) return Discs(work, units, csrf);
            return new Div([new Div(new Button(Icon("settings"), new Text("work.columns")).Attribute("data-columns-open", "").Attribute("data-columns-kind", kind).Attribute("type", "button").Class("button work-tools__columns")).Class("work-seasons__tools"),
                UnitTable(units, work, [], csrf, kind), .. Translations(work, csrf) is { } translations ? new[] { translations } : [], ColumnPicker()]).Class("work-seasons work-volumes");
        }

        // What readers had machine translated of a book, language by language, with a delete that clears one language only; shown to those who may and only with translations present.
        private IUiComponent? Translations(WorkManagementV1 work, string? csrf)
        {
            if (work.Translations.Count == 0 || !texts.Can("media.manual.update", "Library", "Reading", "Translation")) return null;
            var editions = work.Groups.Where(group => group.WorkGroupKindId == WorkGroupKind.Edition).ToDictionary(group => group.Id);
            string From(WorkManagementTranslationV1 item) => item.WorkEditionId is { } id && editions.TryGetValue(id, out var edition)
                ? edition.Name ?? edition.Title ?? (edition.LanguageCode is { } code ? WorkLanguages.Name(WorkLanguages.Code(code)) : texts["work.edition.plain"]) : "";
            var rows = work.Translations.Select(item => (IUiComponent)new Tr(
                new Td(new ValueText(WorkLanguages.Name(WorkLanguages.Code(item.Language)))), new Td(new ValueText(From(item))),
                new Td(new ValueText(item.CoveragePercent.ToString(CultureInfo.InvariantCulture) + " %")), new Td(new ValueText(ByteSize.Format(item.Bytes))),
                new Td(new Div(new PostForm(Address(Tab, "DeleteTranslation"), csrf, [("documentId", item.DocumentId.ToString(CultureInfo.InvariantCulture)), ("language", item.Language)],
                    IconSubmit("trash", "work.translation.delete"))).Class("work-icons"))).Class("work-row")).ToArray();
            var table = new Div(new TableElement([new Thead(new Tr(new Th(new Text("work.col.language")).Attribute("scope", "col"), new Th(new Text("work.translations.source")).Attribute("scope", "col"),
                new Th(new Text("work.translations.coverage")).Attribute("scope", "col"), new Th(new Text("work.col.size")).Attribute("scope", "col"), new Th(new Span()).Attribute("scope", "col"))), new Tbody([.. rows])])
                .Class("work-table work-table--manage")).Class("work-table-wrap");
            return new Div(
                new Div(new Div(new H2(new Text("work.translations")), new Div(new Span(new ValueText(work.Translations.Count.ToString(CultureInfo.InvariantCulture)))).Class("work-coverage")).Class("work-season__lead"),
                    new Button(Icon("chevron-down", 22)).Class("work-season__toggle").Attribute("type", "button").Attribute("data-season-toggle", "work-translations").Attribute("aria-expanded", "false")
                        .Attribute("aria-controls", "work-translations").TextAttribute("aria-label", "work.toggleSeason")).Class("work-season__head"),
                new Div(table).Class("work-season__panel").Attribute("id", "work-translations").Attribute("data-open", "false")).Class("work-season");
        }

        // An album of several discs: one accordion per disc with its tracks.
        private IUiComponent Discs(WorkManagementV1 work, WorkManagementUnitV1[] tracks, string? csrf)
        {
            var content = new List<IUiComponent>();
            var first = true;
            foreach (var disc in tracks.GroupBy(unit => unit.DiscNumber ?? 1).OrderBy(group => group.Key))
            {
                var panelId = "work-disc-" + disc.Key.ToString(CultureInfo.InvariantCulture);
                var open = first;
                first = false;
                var units = disc.ToArray();
                var present = units.Count(unit => unit.WorkUnitStatusId == WorkUnitStatus.Present);
                var bytes = work.Files.Where(file => !file.IsMissing && file.WorkUnitIds.Any(id => units.Any(unit => unit.Id == id))).Sum(file => file.SizeBytes);
                var info = string.Format(CultureInfo.CurrentCulture, texts["work.coverage"], present, units.Length) + (bytes > 0 ? " · " + ByteSize.Format(bytes) : "");
                content.Add(new Div(
                    new Div(
                        new Div(new H2(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["work.disc"], disc.Key))), new Div(new Span(new ValueText(info))).Class("work-coverage")).Class("work-season__lead"),
                        new Button(Icon("chevron-down", 22)).Class("work-season__toggle").Attribute("type", "button").Attribute("data-season-toggle", panelId).Attribute("aria-expanded", open ? "true" : "false")
                            .Attribute("aria-controls", panelId).TextAttribute("aria-label", "work.toggleSeason")).Class("work-season__head"),
                    new Div(UnitTable(units, work, [], csrf, "tracks")).Class("work-season__panel").Attribute("id", panelId).Attribute("data-open", open ? "true" : "false")).Class("work-season"));
            }

            return new Div([new Div(new Button(Icon("settings"), new Text("work.columns")).Attribute("data-columns-open", "").Attribute("data-columns-kind", "tracks").Attribute("type", "button").Class("button work-tools__columns")).Class("work-seasons__tools"),
                .. content, ColumnPicker()]).Class("work-seasons work-discs");
        }

        private IUiComponent UnitTable(WorkManagementUnitV1[] episodes, WorkManagementV1 work, Dictionary<long, decimal> seasonNumbers, string? csrf, string kind = "units")
        {
            var columns = ColumnsOf(work);
            var isVolume = work.MediaTypeId == MediaType.Book;
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var rows = new List<IUiComponent>();
            var strips = new List<IUiComponent>();
            foreach (var episode in episodes)
            {
                var files = work.Files.Where(file => !file.IsMissing && file.WorkUnitIds.Contains(episode.Id)).OrderBy(file => (int)Rank(file.FileQualityGradeId)).ThenByDescending(file => file.SizeBytes).ToArray();
                var best = files.FirstOrDefault();
                var cells = new Dictionary<string, Td>
                {
                    ["monitored"] = Cell("monitored", UnitSwitch(episode, work, csrf)),
                    ["number"] = Cell("number", NumberText(episode), episode.Number.ToString("000000.000", CultureInfo.InvariantCulture)),
                    ["absolute"] = Cell("absolute", new ValueText(episode.AbsoluteNumber?.ToString(CultureInfo.InvariantCulture) ?? ""), (episode.AbsoluteNumber ?? 0).ToString("000000", CultureInfo.InvariantCulture)),
                    ["title"] = Cell("title", new Span(new ValueText(UnitTitle(episode))).Class("work-title-text"), UnitTitle(episode), files.Length > 1 ? VersionsButton(episode.Id, files.Length) : null),
                    [isVolume ? "released" : "aired"] = Cell(isVolume ? "released" : "aired", new ValueText(episode.ReleasedOn?.ToString("d MMM yyyy", CultureInfo.CurrentCulture) ?? ""), episode.ReleasedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""),
                    ["status"] = Cell("status", StatusPill(episode, today), ((int)episode.WorkUnitStatusId).ToString(CultureInfo.InvariantCulture))
                };
                foreach (var (key, cell) in FileCells(files.Length > 1 ? null : best, files.Length > 1 ? files.Sum(file => file.SizeBytes) : null, files.Length > 1 ? files[0] : null)) cells[key] = cell;
                var icons = UnitIcons(episode, files, seasonNumbers, csrf);
                if (icons.Count > 0)
                {
                    var actionsCell = new Td(new Div([.. icons]).Class("work-icons"));
                    actionsCell.Attribute("data-col", "actions");
                    cells["actions"] = actionsCell;
                }

                rows.Add(new Tbody([RowOf(columns, cells), .. files.Length > 1 ? files.Select(file => FileRow(file, episode.Id, columns, csrf)) : []]).Class("work-unit"));
                strips.Add(UnitStrip(episode, files, today, work, seasonNumbers, csrf, columns));
            }

            // A narrow container shows the strips instead of the table.
            return new Div(Table(kind, columns, rows), new Div([.. strips]).Class("work-strips").Attribute("data-work-strips", kind));
        }

        // Anime numbers episodes as the library does or as AniList does; the choice only changes what is shown.
        private IUiComponent NumberingSwitch(WorkManagementV1 work) => work.MediaSubtypeId != MediaSubtype.Anime || !work.Units.Any(unit => unit.AniListNumber is not null) ? new Span()
            : new Div(new Button(new Text("work.numbering.standard")).Attribute("type", "button").Attribute("data-numbering-set", "standard").Attribute("aria-pressed", "true"),
                new Button(new Text("work.numbering.anilist")).Attribute("type", "button").Attribute("data-numbering-set", "anilist").Attribute("aria-pressed", "false")).Class("work-numbering").Attribute("role", "group").TextAttribute("aria-label", "work.numbering.label");

        // The number of a unit; anime may show the AniList number instead, which a switch above the list chooses.
        private IUiComponent NumberText(WorkManagementUnitV1 unit)
        {
            var standard = unit.Number.ToString("0.###", CultureInfo.InvariantCulture);
            return _management?.MediaSubtypeId != MediaSubtype.Anime || unit.WorkUnitTypeId != WorkUnitType.Episode ? new ValueText(standard)
                : new Span(new Span(new ValueText(standard)).Attribute("data-numbering", "standard"), new Span(new ValueText((unit.AniListNumber ?? unit.Number).ToString("0.###", CultureInfo.InvariantCulture))).Attribute("data-numbering", "anilist").Attribute("hidden", "hidden"));
        }

        // A volume without a title of its own is named by its number.
        private string UnitTitle(WorkManagementUnitV1 unit) => unit.Title ?? (unit.WorkUnitTypeId is WorkUnitType.Volume or WorkUnitType.Track ? MediaUnitLabel.Format(texts, unit.WorkUnitTypeId, unit.Number, null) ?? "" : "");

        // Search, interactive search and delete of one unit; each call builds its own forms.
        private List<IUiComponent> UnitIcons(WorkManagementUnitV1 episode, WorkManagementFileV1[] files, Dictionary<long, decimal> seasonNumbers, string? csrf)
        {
            var icons = new List<IUiComponent>();
            if (_wanted is not null && CanAcquire)
            {
                icons.Add(SearchIcon(episode, seasonNumbers, csrf, "work.search.episode", true));
                icons.Add(SearchIcon(episode, seasonNumbers, csrf, "work.interactive.episode", false));
            }

            if (files.Length == 1) icons.AddRange(DeleteIcon(files[0], csrf));
            return icons;
        }

        // A unit on a narrow screen is two lines: the first stays in view (switch, number, name, actions), the second is a strip of facts that slides sideways with those of the other units of its season.
        private IUiComponent UnitStrip(WorkManagementUnitV1 episode, WorkManagementFileV1[] files, DateOnly today, WorkManagementV1 work, Dictionary<long, decimal> seasonNumbers, string? csrf, string[] columns)
        {
            var released = new Fact(new ValueText(episode.ReleasedOn?.ToString("d MMM yyyy", CultureInfo.CurrentCulture) ?? ""), null, episode.ReleasedOn is null);
            var facts = new Dictionary<string, Fact>
            {
                ["aired"] = released,
                ["released"] = released,
                ["absolute"] = new(new ValueText(episode.AbsoluteNumber?.ToString(CultureInfo.InvariantCulture) ?? ""), null, episode.AbsoluteNumber is null),
                ["status"] = new(StatusPill(episode, today))
            };
            foreach (var (key, fact) in FileFacts(files.Length > 1 ? null : files.FirstOrDefault(), files.Length > 1 ? files.Sum(file => file.SizeBytes) : null, files.Length > 1 ? files[0] : null)) facts[key] = fact;
            var title = new List<IUiComponent> { new Span(new ValueText(UnitTitle(episode))).Class("work-title-text") };
            if (files.Length > 1) title.Add(VersionsButton(episode.Id, files.Length));
            var icons = UnitIcons(episode, files, seasonNumbers, csrf);
            var unit = new Div(
                new Div(episode.WorkUnitTypeId == WorkUnitType.Track ? new Span() : UnitSwitch(episode, work, csrf), new Span(NumberText(episode)).Class("work-strip__number"), new Div([.. title]).Class("work-strip__title"),
                    new Div([.. icons]).Class("work-icons")).Class("work-strip__main"),
                StripFacts(facts, columns)).Class("work-strip");
            return new Div([unit, .. files.Length > 1 ? files.Select(file => FileStrip(file, csrf, columns)) : []]).Class("work-strip-group");
        }

        // A version of a unit below its strip, shown with the unit's versions button.
        private IUiComponent FileStrip(WorkManagementFileV1 file, string? csrf, string[] columns, bool hidden = true, bool canEdit = false)
        {
            var icons = DeleteIcon(file, csrf, canEdit).ToArray();
            var strip = new Div(
                new Div(new Span().Class("work-strip__number"), new Div(new Span(new ValueText(Path.GetFileName(file.RelativePath))).Class("work-title-text")).Class("work-strip__title"), new Div(icons).Class("work-icons")).Class("work-strip__main"),
                StripFacts(FileFacts(file, null, file), columns)).Class("work-strip work-strip--file");
            if (hidden) strip.Attribute("hidden", "hidden");
            return strip;
        }

        // The facts of the second line in the order of the columns; one that has nothing to say is left out.
        private IUiComponent StripFacts(Dictionary<string, Fact> facts, string[] columns) =>
            new Div([new Span().Class("work-strip__handle").Attribute("aria-hidden", "true"), .. columns.Where(column => facts.TryGetValue(column, out var fact) && !fact.Empty).Select(column =>
                (IUiComponent)new Span(new Span(new Text("work.col." + column)).Class("work-fact__label"), facts[column].Content).Class("work-fact").Attribute("data-col", column))]).Class("work-strip__facts");

        private IUiComponent FileTable(WorkManagementV1 work, string? csrf) => FileTable(work.Files.Where(file => !file.IsMissing), FileColumns, csrf);

        private IUiComponent FileTable(IEnumerable<WorkManagementFileV1> files, string[] columns, string? csrf, string kind = "files")
        {
            var ordered = files.OrderBy(file => (int)Rank(file.FileQualityGradeId)).ThenByDescending(file => file.SizeBytes).ToArray();
            var rows = ordered.Select(file => (IUiComponent)new Tbody(FileRow(file, null, columns, csrf)).Class("work-unit")).ToList();
            var strips = ordered.Select(file => FileStrip(file, csrf, columns, hidden: false, canEdit: true));
            return new Div(Table(kind, columns, rows), new Div([.. strips]).Class("work-strips").Attribute("data-work-strips", kind));
        }

        // A book's editions or a game's platforms as accordions, each with the files that belong to it; a platform is monitored and searched on its own.
        private IUiComponent Groups(WorkManagementV1 work, string? csrf)
        {
            var isGame = work.MediaTypeId == MediaType.Game;
            var columns = isGame ? GameColumns : EditionColumns;
            var present = work.Files.Where(file => !file.IsMissing).ToArray();
            var content = new List<IUiComponent>();
            var first = true;
            var listed = work.Groups.Select(group => group.Id).ToHashSet();
            var others = present.Where(file => file.GroupId is not { } id || !listed.Contains(id)).ToArray();
            if (work.Groups.Count == 0 && others.Length == 0) return new P(new Text("work.empty.groups")).Attribute("role", "status");
            void Add(string? panelKey, string name, string info, IUiComponent? lead, IEnumerable<IUiComponent> actions, WorkManagementFileV1[] files, string[]? groupColumns = null)
            {
                var panelId = "work-group-" + panelKey;
                var open = first;
                first = false;
                content.Add(new Div(
                    new Div(
                        new Div([.. lead is null ? [] : new[] { lead }, new H2(new ValueText(name)), new Div(new Span(new ValueText(info))).Class("work-coverage")]).Class("work-season__lead"),
                        new Button(Icon("chevron-down", 22)).Class("work-season__toggle").Attribute("type", "button").Attribute("data-season-toggle", panelId).Attribute("aria-expanded", open ? "true" : "false")
                            .Attribute("aria-controls", panelId).TextAttribute("aria-label", "work.toggleSeason"),
                        new Div([.. actions]).Class("work-season__actions work-icons")).Class("work-season__head"),
                    new Div(files.Length == 0 ? new P(new Text("work.empty.noFile")).Attribute("role", "status") : FileTable(files, groupColumns ?? columns, csrf, isGame ? "games" : groupColumns is not null && groupColumns == AudiobookColumns ? "audiobooks" : "editions")).Class("work-season__panel").Attribute("id", panelId)
                        .Attribute("data-open", open ? "true" : "false")).Class("work-season"));
            }

            foreach (var group in work.Groups)
            {
                var files = present.Where(file => file.GroupId == group.Id).ToArray();
                var size = files.Length > 0 ? ByteSize.Format(files.Sum(file => file.SizeBytes)) : null;
                if (isGame)
                {
                    var icons = new List<IUiComponent>();
                    IUiComponent? lead = null;
                    if (group.WantedItemId is { } wantedId && _wanted is not null && CanAcquire)
                    {
                        var id = wantedId.ToString(CultureInfo.InvariantCulture);
                        lead = new PostForm(Address(Tab, "Monitor"), csrf, [("isPaused", group.IsPaused == true ? "false" : "true"), ("wantedItemId", id)],
                            new SwitchButton(group.IsPaused != true, group.IsPaused == true ? "work.monitor.start" : "work.monitor.stop"));
                        icons.Add(new PostForm(Address(Tab, "Automatic"), csrf, [("wantedItemId", id)], IconSubmit("search", "work.search")));
                        icons.Add(new A(Icon("user", 22)).Class("work-icon").Attribute("href", WantedStatus.Home(wantedId) + "/Search").TextAttribute("aria-label", "work.interactive").TextAttribute("title", "work.interactive"));
                        if (_platformTargets.ContainsKey(wantedId)) icons.Add(IconSubmit("pencil", "work.edit").Attribute("type", "button").Attribute("data-dialog-open", "work-edit-" + id));
                    }

                    Add(group.Id.ToString(CultureInfo.InvariantCulture), group.Name ?? "", string.Join(" · ", new[] { group.Title, group.WantedItemId is null ? texts["work.platform.notWanted"] : null, size }.Where(text => !string.IsNullOrEmpty(text))), lead, icons, files);
                }
                else
                {
                    var facts = new List<string>();
                    if (group.LanguageCode is { } language) facts.Add(WorkLanguages.Name(WorkLanguages.Code(language)));
                    if (group.EditionFormatId is { } format) facts.Add(texts["work.edition.format." + format]);
                    if (group.Publisher is { } publisher) facts.Add(publisher + (group.ReleaseYear is { } year ? ", " + year.ToString(CultureInfo.InvariantCulture) : ""));
                    else if (group.ReleaseYear is { } releaseYear) facts.Add(releaseYear.ToString(CultureInfo.InvariantCulture));
                    if (group.Isbn is { } isbn) facts.Add("ISBN " + isbn);
                    if (size is not null) facts.Add(size);
                    IUiComponent? lead = null;
                    var icons = new List<IUiComponent>();
                    if (texts.CanManageMedia())
                    {
                        lead = new PostForm(Address(Tab, "EditionMonitor"), csrf, [("workEditionId", group.Id.ToString(CultureInfo.InvariantCulture)), ("isMonitored", group.IsPaused == true ? "true" : "false")],
                            new SwitchButton(group.IsPaused != true, group.IsPaused == true ? "work.edition.monitor" : "work.edition.unmonitor"));
                    }

                    if (_wanted is not null && CanAcquire && group.LanguageCode is { } searchLanguage)
                    {
                        icons.Add(new PostForm(Address(Tab, "Automatic"), csrf, [("language", searchLanguage)], IconSubmit("search", "work.edition.search")));
                    }

                    if (files.Length == 0 && group.LanguageCode is { } wanted && work.Translations.Any(item => WorkLanguages.Code(item.Language) == WorkLanguages.Code(wanted))) facts.Add(texts["work.edition.readsTranslated"]);
                    Add(group.Id.ToString(CultureInfo.InvariantCulture), group.Name ?? group.Title ?? texts["work.edition.plain"], string.Join(" · ", facts), lead, icons,
                        files, group.EditionFormatId == EditionFormat.Audiobook ? AudiobookColumns : columns);
                }
            }

            if (others.Length > 0) Add("other", texts["work.group.other"], ByteSize.Format(others.Sum(file => file.SizeBytes)), null, [], others);
            if (Translations(work, csrf) is { } translations) content.Add(translations);
            content.Add(ColumnPicker());
            return new Div([.. content]).Class("work-seasons work-groups");
        }

        // A file in the columns of its table, for a film or below the unit that has several.
        private IUiComponent FileRow(WorkManagementFileV1 file, long? unitId, string[] columns, string? csrf)
        {
            var cells = new Dictionary<string, Td> { ["title"] = Cell("title", new Span(new ValueText(Path.GetFileName(file.RelativePath))).Class("work-title-text"), Path.GetFileName(file.RelativePath)) };
            foreach (var (key, cell) in FileCells(file, null, file)) cells[key] = cell;
            var icons = DeleteIcon(file, csrf, canEdit: unitId is null).ToArray();
            if (icons.Length > 0)
            {
                var actionsCell = new Td(new Div(icons).Class("work-icons"));
                actionsCell.Attribute("data-col", "actions");
                cells["actions"] = actionsCell;
            }
            var row = RowOf(columns, cells).Class("work-file");
            if (unitId is { } id) row.Attribute("data-unit", id.ToString(CultureInfo.InvariantCulture)).Attribute("hidden", "hidden");
            return row;
        }

        // Quality, size and languages of one file; several versions show their total size and the best one's quality.
        private Dictionary<string, Td> FileCells(WorkManagementFileV1? file, long? totalBytes, WorkManagementFileV1? best) =>
            FileFacts(file, totalBytes, best).ToDictionary(item => item.Key, item => Cell(item.Key, item.Value.Content, item.Value.Sort));

        // What a table cell and a strip both show of a file: its content, the value it sorts by and whether there is anything to show.
        private Dictionary<string, Fact> FileFacts(WorkManagementFileV1? file, long? totalBytes, WorkManagementFileV1? best)
        {
            var facts = new Dictionary<string, Fact>();
            var shown = file ?? best;
            if (shown is null) return facts;
            var codec = string.Join(" ", new[] { shown.VideoCodec, shown.Height is { } height ? height.ToString(CultureInfo.InvariantCulture) + "p" : null }.Where(text => text is not null));
            if (codec.Length == 0 && shown.AudioTracks.Count > 0 && shown.AudioTracks[0].Codec is { } audioCodec) codec = audioCodec;
            var perHour = shown.DurationMs is > 0 ? (decimal)(totalBytes ?? shown.SizeBytes) / 1048576m / (shown.DurationMs.Value / 3_600_000m) : (decimal?)null;
            var noLanguages = shown.AudioLanguages.Length + shown.SubtitleLanguages.Length == 0;
            facts["quality"] = new(QualityBadge(shown), ((int)Rank(shown.FileQualityGradeId)).ToString(CultureInfo.InvariantCulture), shown.QualityKey is null);
            facts["size"] = new(SizeBlock(shown, totalBytes), (totalBytes ?? shown.SizeBytes).ToString("D20", CultureInfo.InvariantCulture));
            facts["group"] = new(new ValueText(shown.ReleaseGroup ?? ""), shown.ReleaseGroup ?? "", string.IsNullOrEmpty(shown.ReleaseGroup));
            facts["languages"] = new(LanguageGroup(shown, WorkLanguages.Mode.Flags), null, noLanguages);
            facts["languagesIso"] = new(LanguageGroup(shown, WorkLanguages.Mode.Iso), null, noLanguages);
            facts["languagesNames"] = new(LanguageGroup(shown, WorkLanguages.Mode.Names), null, noLanguages);
            facts["length"] = new(new ValueText(shown.DurationMs is { } milliseconds ? Duration(milliseconds) : ""), (shown.DurationMs ?? 0).ToString("D12", CultureInfo.InvariantCulture), shown.DurationMs is null);
            facts["perhour"] = new(new ValueText(perHour is { } mb ? mb.ToString("0", CultureInfo.CurrentCulture) + " MB/h" : ""), (perHour ?? 0).ToString("000000000.0", CultureInfo.InvariantCulture), perHour is null);
            facts["format"] = new(new ValueText(Path.GetExtension(shown.RelativePath).TrimStart('.').ToUpperInvariant()), Path.GetExtension(shown.RelativePath).TrimStart('.').ToUpperInvariant(), Path.GetExtension(shown.RelativePath).Length < 2);
            facts["pages"] = new(new ValueText(shown.PageCount?.ToString(CultureInfo.InvariantCulture) ?? ""), (shown.PageCount ?? 0).ToString("D8", CultureInfo.InvariantCulture), shown.PageCount is null);
            facts["region"] = new(new ValueText(shown.GameRegion ?? ""), shown.GameRegion ?? "", string.IsNullOrEmpty(shown.GameRegion));
            facts["version"] = new(new ValueText(shown.GameVersion ?? ""), shown.GameVersion ?? "", string.IsNullOrEmpty(shown.GameVersion));
            facts["rom"] = new(shown.IsRomVerified is null ? new Span() : new Span(new Text(shown.IsRomVerified == true ? "work.rom.verified" : "work.rom.unverified")).Class("work-rom work-rom--" + (shown.IsRomVerified == true ? "ok" : "off")),
                shown.IsRomVerified == true ? "1" : "0", shown.IsRomVerified is null);
            facts["path"] = new(new ValueText(shown.RelativePath));
            facts["codec"] = new(new ValueText(codec), null, codec.Length == 0);
            facts["range"] = new(new ValueText(shown.DynamicRange ?? ""), null, string.IsNullOrEmpty(shown.DynamicRange));
            return facts;
        }

        private static string Duration(long milliseconds)
        {
            var time = TimeSpan.FromMilliseconds(milliseconds);
            return time.TotalHours >= 1 ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)time.TotalHours, time.Minutes, time.Seconds) : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", time.Minutes, time.Seconds);
        }

        private sealed record Fact(IUiComponent Content, string? Sort = null, bool Empty = false);

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
            var scope = unit.WorkUnitTypeId == WorkUnitType.Volume
                ? _management?.MediaSubtypeId == MediaSubtype.Manga && Whole(unit.Number) is { } volume ? new SearchWantedDataV1(null, volume) : null
                : unit.WorkUnitTypeId == WorkUnitType.Season
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
        private IUiComponent Table(string kind, string[] columns, List<IUiComponent> bodies, bool striped = true)
        {
            var head = new Tr([.. columns.Select(column => (IUiComponent)new Th(SortableColumns.Contains(column) ? (IUiComponent)new Button(new Text("work.col." + column)).Attribute("data-sort", column).Attribute("type", "button").Class("work-sort")
                    : new Text("work.col." + column)).Attribute("data-col", column).Attribute("data-label", texts["work.col." + column]).Attribute("scope", "col")
                .Attribute("data-hidden", HiddenByDefault.Contains(column) ? "true" : "false")),
                new Th(new Button(Icon("settings"), new Span(new Text("work.columns")).Class("visually-hidden")).Attribute("data-columns-open", "").Attribute("type", "button").Class("work-gear")).Attribute("data-col", "actions")]);
            return new Div(new TableElement([new Thead(head), .. bodies]).Class("work-table").Attribute("data-work-table", kind)).Class("work-table-wrap work-table-wrap--" + kind + (striped ? " work-table-wrap--striped" : ""));
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

        // A file goes at once, as in Sonarr; what happens to it follows the setting of replaced files.
        private IEnumerable<IUiComponent> DeleteIcon(WorkManagementFileV1 file, string? csrf, bool canEdit = false)
        {
            if (!texts.CanManageMedia()) yield break;
            if (canEdit) yield return IconSubmit("pencil", "work.file.edit").Attribute("type", "button").Attribute("data-edit-file", file.Id.ToString(CultureInfo.InvariantCulture));
            yield return new PostForm(Address(Tab, "DeleteFiles"), csrf, [("storedFileIds", file.Id.ToString(CultureInfo.InvariantCulture))], IconSubmit("trash", "work.file.delete"));
        }

        // All files of a series with a box each: delete the chosen ones, and rename every file whose name differs from the naming.
        private IUiComponent ManageFiles(WorkManagementV1 work, string? csrf)
        {
            var files = work.Files.Where(file => !file.IsMissing).ToArray();
            if (files.Length == 0) return new P(new Text("work.manage.empty")).Attribute("role", "status");
            var targets = _renames is null ? new Dictionary<long, string>() : _renames.Items.ToDictionary(item => item.StoredFileId, item => item.TargetPath);
            var seasons = work.Units.Where(unit => unit.WorkUnitTypeId == WorkUnitType.Season).ToDictionary(unit => unit.Id, unit => unit.Number);
            var episodes = work.Units.Where(unit => unit.WorkUnitTypeId is WorkUnitType.Episode or WorkUnitType.Volume or WorkUnitType.Track).ToDictionary(unit => unit.Id);
            var video = work.MediaTypeId is MediaType.Movie or MediaType.Series;
            decimal SeasonOf(WorkManagementUnitV1 unit) => unit.ParentWorkUnitId is { } parent && seasons.TryGetValue(parent, out var season) ? season : 0;
            string Label(WorkManagementFileV1 file) => string.Join(", ", file.WorkUnitIds.Where(episodes.ContainsKey).Select(id => episodes[id]).OrderBy(SeasonOf).ThenBy(unit => unit.Number)
                .Select(unit => MediaUnitLabel.Format(texts, unit.WorkUnitTypeId, unit.Number, unit.WorkUnitTypeId is WorkUnitType.Volume or WorkUnitType.Track ? null : SeasonOf(unit)) ?? ""));
            decimal Position(WorkManagementFileV1 file) => file.WorkUnitIds.Where(episodes.ContainsKey).Select(id => SeasonOf(episodes[id]) * 100000 + episodes[id].Number).DefaultIfEmpty(decimal.MaxValue).Min();

            var rows = files.OrderBy(Position).ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Select(file =>
            {
                var name = new List<IUiComponent> { new Span(new ValueText(file.RelativePath)).Class("work-manage__path") };
                if (_renames is not null)
                {
                    // Only a complete plan can say that a file needs no rename.
                    if (targets.TryGetValue(file.Id, out var target)) name.Add(new Span(new Text("work.manage.renamesTo"), new ValueText(" " + target)).Class("work-manage__target"));
                    else if (_renames.Total <= _renames.Items.Count) name.Add(new Span(new Text("work.manage.matches")).Class("work-manage__matches"));
                }

                return (IUiComponent)new Tr(
                    new Td(new Input().Attribute("type", "checkbox").Attribute("name", "storedFileIds").Attribute("value", file.Id.ToString(CultureInfo.InvariantCulture)).TextAttribute("aria-label", "work.manage.select")),
                    new Td(new ValueText(Label(file))),
                    new Td(new Div([.. name]).Class("work-manage__file")),
                    new Td(QualityBadge(file)),
                    video ? new Td(LanguageGroup(file, WorkLanguages.Mode.Flags)) : new Td(new Span()),
                    new Td(SizeBlock(file, null))).Class("work-row");
            }).ToArray();

            var head = new Tr(
                new Th(new Input().Attribute("type", "checkbox").Attribute("data-select-all", "").TextAttribute("aria-label", "work.manage.selectAll")).Attribute("scope", "col"),
                new Th(new Text("work.manage.col.unit")).Attribute("scope", "col"), new Th(new Text("work.manage.col.file")).Attribute("scope", "col"), new Th(new Text("work.col.quality")).Attribute("scope", "col"),
                new Th(new Text(video ? "work.col.languages" : "work.col.format")).Attribute("scope", "col"), new Th(new Text("work.col.size")).Attribute("scope", "col"));
            var table = new Div(new TableElement([new Thead(head), new Tbody([.. rows])]).Class("work-table work-table--manage")).Class("work-table-wrap work-table-wrap--striped");
            string[] stripColumns = video ? ["quality", "size", "languages"] : ["quality", "size", "format"];
            var strips = new Div([new Label(new Input().Attribute("type", "checkbox").Attribute("data-select-all", ""), new Span(new Text("work.manage.selectAll"))).Class("work-strips__all"),
                .. files.OrderBy(Position).ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Select(file =>
                {
                    var plan = targets.TryGetValue(file.Id, out var renamed) ? renamed : null;
                    return (IUiComponent)new Div(
                        new Div(new Input().Attribute("type", "checkbox").Attribute("name", "storedFileIds").Attribute("value", file.Id.ToString(CultureInfo.InvariantCulture)).TextAttribute("aria-label", "work.manage.select"),
                            new Span(new ValueText(Label(file))).Class("work-strip__number"),
                            new Div(new Span(new ValueText(Path.GetFileName(file.RelativePath))).Class("work-title-text"),
                                plan is null ? new Span() : new Span(new Text("work.manage.renamesTo"), new ValueText(" " + Path.GetFileName(plan))).Class("work-manage__target")).Class("work-strip__title work-strip__title--stacked"),
                            new Span()).Class("work-strip__main"),
                        StripFacts(FileFacts(file, null, file), stripColumns)).Class("work-strip");
                })]).Class("work-strips").Attribute("data-work-strips", "manage");

            var content = new List<IUiComponent>();
            if (_renames is { Total: > 0 } plan)
            {
                content.Add(new PostForm(Address(WorkTab.Files, "RenameFiles"), csrf, [("fingerprint", plan.Fingerprint)],
                    new Button(Icon("tag"), new ValueText(string.Format(CultureInfo.CurrentCulture, texts["work.manage.rename"], plan.Total))).Attribute("type", "submit")).Class("work-manage__bar"));
            }

            content.Add(new PostForm(Address(WorkTab.Files, "DeleteFiles"), csrf, [], new Div(new Button(Icon("pencil"), new Text("work.manage.edit")).Attribute("type", "button").Attribute("data-edit-selected", ""),
                new Button(Icon("trash"), new Text("work.manage.delete")).Attribute("type", "submit")).Class("work-manage__bar"), table, strips));
            return new Div([.. content]).Class("work-manage");
        }

        private static IUiComponent ColumnPicker() => new Framework.ColumnPicker();

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
        private IUiComponent EditDialog(WorkManagementV1 work, string? csrf, WantedDetailsV1 wanted, string dialogId, bool ownTarget = false)
        {
            var isOpen = _choice is not null && FormError is not null && (_choiceWantedId ?? _wanted?.Id) == wanted.Id;
            var mine = isOpen ? _choice : null;
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
                form.Cancel();
                form.Submit("common.save");
            }, Address(Tab, "Choice") + (ownTarget ? "&wantedItemId=" + wanted.Id.ToString(CultureInfo.InvariantCulture) : ""), csrf,
                mine ?? new UpdateWantedDataV1(wanted.IsPaused, wanted.Version, wanted.AcquisitionProfileId, wanted.LibraryRootId, wanted.MonitorsNewUnits, tagIds: wanted.TagIds), mine is null ? null : FormError);
            return new Modal(assets, dialogId, "work.edit", [form], isOpen);
        }

        // Sonarr's dialog: the title leaves the library, with its files when ticked, and with an exclusion when ticked.
        private IUiComponent DeleteDialog(string? csrf)
        {
            var isOpen = _deleteInput is not null && FormError is not null;
            var form = new Form<DeleteWorkDataV1>(form =>
            {
                form.Toggle(data => data.DeletesFiles, "work.delete.files");
                form.Toggle(data => data.AddsExclusion, "work.delete.exclusion");
                form.Cancel();
                form.Submit("work.delete.title");
            }, Address(Tab, "DeleteTitle"), csrf, _deleteInput ?? new DeleteWorkDataV1(false, false), _deleteInput is null ? null : FormError);
            return new Modal(assets, "work-delete", "work.delete.title", [form], isOpen);
        }

        // Quality and audio language of the chosen files; what is left unchanged stays as it is. The boxes or the row's pencil fill in the files.
        private IUiComponent EditFilesDialog(WorkManagementV1 work, string? csrf)
        {
            var qualities = new[] { new SelectOption("", texts["work.files.unchanged"]) }.Concat(ProfileQualities.For(work.MediaTypeId, work.MediaSubtypeId).Select(quality => new SelectOption(quality, quality)));
            var languages = new[] { new SelectOption("", texts["work.files.unchanged"]) }.Concat(FileLanguages.Known.Select(language => new SelectOption(language.Three, WorkLanguages.Name(language.Two))));
            var form = new Form<UpdateLibraryFileFactsDataV1>(form =>
            {
                form.Content(new Div().Attribute("data-edit-ids", ""));
                form.Select(data => data.QualityKey, "work.files.quality", [.. qualities]);
                if (work.MediaTypeId is MediaType.Movie or MediaType.Series) form.Select(data => data.AudioLanguage, "work.files.audioLanguage", [.. languages]);
                form.Cancel();
                form.Submit("common.save");
            }, Address(Tab, "EditFiles"), csrf, new UpdateLibraryFileFactsDataV1([]));
            // A file with several audio tracks offers each its own language, shown when it alone is chosen.
            var templates = work.MediaTypeId is MediaType.Movie or MediaType.Series
                ? work.Files.Where(file => !file.IsMissing && file.AudioTracks.Count > 1).Select(file => (IUiComponent)new Template(new Fieldset([new Legend(new Text("work.files.tracks")),
                    .. file.AudioTracks.Select(track => (IUiComponent)new Label(
                        new Span(new ValueText(string.Format(CultureInfo.CurrentCulture, texts["work.files.track"], track.StreamIndex + 1) + (track.Codec is null ? "" : " · " + track.Codec) + (track.Channels is { } channels ? " " + channels.ToString(CultureInfo.InvariantCulture) + "ch" : ""))),
                        new Select([new Option(new ValueText(texts["work.files.unchanged"])).Attribute("value", ""),
                            .. FileLanguages.Known.Select(language => new Option(new ValueText(WorkLanguages.Name(language.Two))).Attribute("value", language.Three)
                                .Attribute("data-current", track.Language == language.Three ? "true" : "false"))]).Attribute("name", "trackLanguage-" + track.StreamIndex.ToString(CultureInfo.InvariantCulture))
                            .Attribute("data-current", track.Language ?? "")).Class("work-track"))]).Class("work-tracks")).Attribute("data-tracks-for", file.Id.ToString(CultureInfo.InvariantCulture)))
                : [];
            return new Modal(assets, "work-files-edit", "work.files.editTitle", [form, .. templates]);
        }

        private Button Labeled(string icon, string labelKey) => new(Icon(icon), new Text(labelKey));

        private IUiComponent Icon(string name, int size = 20) => new Framework.Icon(name, assets, size, "2.4");

        private HtmlElement IconSubmit(string icon, string labelKey) => new IconButton(Icon(icon, 22)).Attribute("type", "submit").Class("work-icon").TextAttribute("aria-label", labelKey).TextAttribute("title", labelKey);


        private sealed class Fieldset(params IUiComponent[] children) : HtmlElement("fieldset", children);

        private sealed class Legend(params IUiComponent[] children) : HtmlElement("legend", children);

        private sealed class IconButton(params IUiComponent[] children) : HtmlElement("button", children);
    }
}
