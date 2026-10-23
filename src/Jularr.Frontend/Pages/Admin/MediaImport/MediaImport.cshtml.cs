using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.MediaImport
{
    // A list of titles added at once: the file, what each entry is and would do, and what became of each.
    [RequestFormLimits(ValueCountLimit = 20_000, ValueLengthLimit = 4_194_304)]
    public sealed class MediaImportModel(PreviewMediaImportV1 preview, RunMediaImportV1 run, IAntiforgery antiforgery, UiText texts, FrontendAssets assets) : JularrPage
    {
        private const string Address = "/Admin/Import";
        private MediaImportDataV1? _input;
        private MediaImportPreviewV1? _preview;
        private MediaImportResultV1? _result;
        private Dictionary<int, MediaImportChoiceV1> _posted = [];

        protected override void Define(Page page)
        {
            page.Title("admin.import.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/MediaImport/MediaImport.entry.scss");
            page.Asset("Pages/Admin/MediaImport/MediaImport.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken!;
            var content = new List<IUiComponent>();
            if (_result is { } result) Result(content, result, csrf);
            else if (_preview is { } listing) Preview(content, listing, csrf);
            else
            {
                page.Asset("Framework/Components/FileInput.entry.ts");
                page.Asset("Framework/Components/CodeSample.entry.ts");
                Upload(content, csrf);
            }

            page.Content.Add(new Div([.. content]).Class("media-import"));
        }

        // The list as a file or as text, and how titles are added.
        private void Upload(List<IUiComponent> content, string csrf)
        {
            // Script reads the list and says whether it can be read; without script nothing is claimed.
            var status = new Span(new Text("import.list.empty")).Class("media-import__status").Attribute("hidden", "hidden").Attribute("data-import-status", "").Attribute("data-state", "empty")
                .TextAttribute("data-empty", "import.list.empty").TextAttribute("data-valid", "import.list.valid").TextAttribute("data-invalid", "import.list.invalid");
            var sample = new Div(new CodeSample("import.sample", MediaImportExample.Json, assets),
                new A(new Icon("download", assets, 14), new Text("import.example")).Class("button").Attribute("href", Address + "?handler=Example").Attribute("download", MediaImportExample.FileName))
                .Class("media-import__sample ui-field--wide");
            content.Add(new Div(new Form<MediaImportDataV1>(form =>
            {
                form.Section("import.source", source =>
                {
                    source.File(item => item.File, "import.file", assets);
                    source.Content(new Div(new Span(new Text("import.or"))).Class("media-import__divider ui-field--wide"));
                    source.Multiline(item => item.Json, "import.json");
                    source.Note(status);
                    source.Submit(new Button(new Text("import.preview")).Primary());
                });
                form.Section("import.behavior", behavior =>
                {
                    behavior.Select(item => item.Action, "import.action", ActionOptions());
                    behavior.Content(sample);
                });
            }, Address + "?handler=Preview", csrf, _input ?? new MediaImportDataV1(null, null, MediaImportActionType.MonitorAndSearch), FormError)).Class("media-import__upload"));
        }

        private void Preview(List<IUiComponent> content, MediaImportPreviewV1 listing, string csrf)
        {
            var rows = listing.Rows;
            var summary = Enum.GetValues<MediaImportStatusType>().Select(status => (Status: status, Count: rows.Count(row => row.StatusTypeId == status))).Where(item => item.Count > 0)
                .Select(item => (IUiComponent)new Tag(new Strong(new ValueText(item.Count.ToString(CultureInfo.CurrentCulture))), new Text("import.status." + item.Status))).ToList();
            if (listing.Unsearched > 0) summary.Add(new Tag(new Strong(new ValueText(listing.Unsearched.ToString(CultureInfo.CurrentCulture))), new Text("import.unsearched")));
            summary.Add(new A(new Text("import.restart")).Class("button button--compact").Attribute("href", Address));
            content.Add(new Div(new H2(new Text("import.previewTitle")), new Div([.. summary]).Class("media-import__summary")).Class("media-import__head"));

            // The defaults of each kind of title the list holds.
            var action = _input?.Action ?? MediaImportActionType.MonitorAndSearch;
            var kinds = rows.Where(row => row.MediaTypeId is not null).GroupBy(row => row.TypeKey, StringComparer.Ordinal).Select(group => group.First()).ToDictionary(row => row.TypeKey, StringComparer.Ordinal);
            var defaults = new BaseDataList<MediaImportDefaultV1>(kinds.Keys.Select(key => _input?.Defaults.FirstOrDefault(item => item.Key == key) ?? new MediaImportDefaultV1(key, null, null)));
            var selectable = rows.Where(CanInclude).ToArray();
            var anyProfile = rows.Any(row => CanChoose(row) && Fitting(listing.Profiles, row).Count() > 1);
            var anyLibrary = rows.Any(row => CanChoose(row) && Fitting(listing.Libraries, row).Count() > 1);
            content.Add(new Div(new Form<MediaImportDataV1>(form =>
            {
                form.Hidden(item => item.Json);
                form.Section("import.behavior", behavior =>
                {
                    behavior.Select(item => item.Action, "import.action", ActionOptions());
                    if (kinds.Keys.Any(key => HasChoice(listing, kinds[key])))
                    {
                        behavior.Rows(item => item.Defaults, (row, item) =>
                        {
                            var kind = kinds[item.Key];
                            row.Hidden(value => value.Key);
                            row.Content(new Strong(new Text(MediaTypeLabel.Key(kind.MediaTypeId!.Value, kind.MediaSubtypeId))));
                            if (Fitting(listing.Profiles, kind).Count() > 1) row.Select(value => value.AcquisitionProfileId, "import.profile", Options(Fitting(listing.Profiles, kind), "import.defaultProfile"));
                            if (Fitting(listing.Libraries, kind).Count() > 1) row.Select(value => value.LibraryRootId, "import.library", Options(Fitting(listing.Libraries, kind), "import.defaultLibrary"));
                        }, item => kinds.TryGetValue(item.Key, out var kind) && HasChoice(listing, kind));
                    }
                });

                // One row per title; a column of its own for a profile or library only where some title has the choice.
                var table = new Table<MediaImportRowV1>(rows)
                    .Content("import.col.include", IncludeCell)
                    .Content("media.title", TitleCell)
                    .Content("table.status", StatusCell)
                    .Content("import.col.match", MatchCell)
                    .Content("import.col.options", row => ActionCell(row, action));
                if (anyProfile) table = table.Content("import.profile", row => Picker(row, "AcquisitionProfileId", "import.profile", "import.defaultProfile", listing.Profiles, _posted.GetValueOrDefault(row.Index)?.AcquisitionProfileId));
                if (anyLibrary) table = table.Content("import.library", row => Picker(row, "LibraryRootId", "import.library", "import.defaultLibrary", listing.Libraries, _posted.GetValueOrDefault(row.Index)?.LibraryRootId));
                form.Content(new Div(table).Class("ui-field--wide media-import__rows"));

                // The dock: what is chosen, and the two things to do with it.
                if (selectable.Length > 0)
                {
                    var all = new Toggle("import.selectAll", selectable.All(row => row.IsIncluded));
                    all.Control.Attribute("data-import-all", "");
                    form.Note(new Div(all, new Span(new ValueText("")).Class("media-import__count").Attribute("data-import-count", "").Attribute("hidden", "hidden").TextAttribute("data-template", "import.selected")));
                }

                form.Submit(new Button(new Text(listing.Unsearched > 0 ? "import.searchMore" : "import.refresh")));
                var apply = new Button(new Text("import.apply")).Primary();
                apply.Attribute("formaction", Address + "?handler=Apply");
                apply.Attribute("data-import-apply", "");
                form.Submit(apply);
            }, Address + "?handler=Preview", csrf, new MediaImportDataV1(null, listing.Json, action, defaults), FormError)).Class("media-import__preview"));
        }

        private void Result(List<IUiComponent> content, MediaImportResultV1 result, string csrf)
        {
            var rows = result.Rows;
            var summary = Enum.GetValues<MediaImportOutcomeType>().Select(outcome => (Outcome: outcome, Count: rows.Count(row => row.OutcomeTypeId == outcome))).Where(item => item.Count > 0)
                .Select(item => (IUiComponent)new Tag(new Strong(new ValueText(item.Count.ToString(CultureInfo.CurrentCulture))), new Text("import.outcome." + item.Outcome))).ToList();
            if (result.RemainingJson is { } remaining)
            {
                summary.Add(new PostForm(Address + "?handler=Preview", csrf, Continuation(result, remaining), new Button(new Text("import.continue")).Primary().Submit()));
            }

            summary.Add(new A(new Text("import.again")).Class("button button--compact").Attribute("href", Address));
            content.Add(new Div(new H2(new Text("import.resultTitle")), new Div([.. summary]).Class("media-import__summary")).Class("media-import__head"));
            var shown = rows.Where(row => row.OutcomeTypeId != MediaImportOutcomeType.Skipped).ToArray();
            content.Add(new Div(new Table<MediaImportResultRowV1>(shown)
                .Content("media.title", row => new Div(new Truncated(row.Title ?? "—")).Class("media-import__title"))
                .Content("table.status", row => row.Searches
                    ? new Div(Chip(OutcomeChip(row.OutcomeTypeId), "import.outcome." + row.OutcomeTypeId), Chip(("info", "search"), "import.searches")).Class("media-import__chips")
                    : Chip(OutcomeChip(row.OutcomeTypeId), "import.outcome." + row.OutcomeTypeId))
                .Content("common.actions", row => row.WorkId is { } id
                    ? new A(new Text("common.open")).Class("button button--compact").Attribute("href", "/Admin/Works/" + id.ToString(CultureInfo.InvariantCulture))
                    : new Nothing())).Class("media-import__rows"));
        }

        // The rest of the list goes on with what the person chose: the defaults and each waiting row's own action, profile and library, now at their new places.
        private IEnumerable<(string Name, string Value)> Continuation(MediaImportResultV1 result, string remaining)
        {
            yield return ("json", remaining);
            yield return ("action", ((byte)(_input?.Action ?? MediaImportActionType.MonitorAndSearch)).ToString(CultureInfo.InvariantCulture));
            var position = 0;
            foreach (var item in (IEnumerable<MediaImportDefaultV1>?)_input?.Defaults ?? [])
            {
                var name = "Defaults[" + position++.ToString(CultureInfo.InvariantCulture) + "].";
                yield return (name + "Key", item.Key);
                if (item.AcquisitionProfileId is { } profile) yield return (name + "AcquisitionProfileId", profile.ToString(CultureInfo.InvariantCulture));
                if (item.LibraryRootId is { } library) yield return (name + "LibraryRootId", library.ToString(CultureInfo.InvariantCulture));
            }

            // The rows a run leaves waiting come back in the order of their list, as the first rows of the next one.
            position = 0;
            foreach (var row in result.Rows.Where(row => row.OutcomeTypeId is MediaImportOutcomeType.Deferred or MediaImportOutcomeType.Unavailable))
            {
                var name = "Choices[" + position.ToString(CultureInfo.InvariantCulture) + "].";
                yield return (name + "Index", position.ToString(CultureInfo.InvariantCulture));
                position++;
                if (!_posted.TryGetValue(row.Index, out var posted)) continue;
                if (posted.Action is { } action) yield return (name + "Action", ((byte)action).ToString(CultureInfo.InvariantCulture));
                if (posted.AcquisitionProfileId is { } profile) yield return (name + "AcquisitionProfileId", profile.ToString(CultureInfo.InvariantCulture));
                if (posted.LibraryRootId is { } library) yield return (name + "LibraryRootId", library.ToString(CultureInfo.InvariantCulture));
            }
        }

        // The status as a pill with its icon: green for what works, lavender for what is there, amber for what needs a look, red for what cannot be used.
        private IUiComponent Chip((string Tone, string Icon) look, string key) =>
            new Span(new Icon(look.Icon, assets, 12), new Span(new Text(key))).Class("state-chip state-chip--icon" + (look.Tone.Length > 0 ? " state-chip--" + look.Tone : ""));

        private static (string Tone, string Icon) StatusChip(MediaImportStatusType status) => status switch
        {
            MediaImportStatusType.New => ("available", "plus"),
            MediaImportStatusType.InLibrary => ("", "check"),
            MediaImportStatusType.Duplicate => ("warning", "copy"),
            MediaImportStatusType.Open => ("warning", "search"),
            _ => ("danger", "close")
        };

        private static (string Tone, string Icon) OutcomeChip(MediaImportOutcomeType outcome) => outcome switch
        {
            MediaImportOutcomeType.Added or MediaImportOutcomeType.AddedAndMonitored or MediaImportOutcomeType.Monitored => ("available", "check"),
            MediaImportOutcomeType.Unchanged or MediaImportOutcomeType.Skipped => ("", "check"),
            MediaImportOutcomeType.Unavailable or MediaImportOutcomeType.Deferred => ("warning", "info"),
            _ => ("danger", "close")
        };

        private static string TypeIcon(string typeKey) => typeKey switch
        {
            "movie" or "series" or "anime" => "play",
            "book" or "manga" or "lightnovel" => "books",
            "music" => "headphones",
            "game" => "gamepad",
            _ => "document"
        };

        // The switch that includes the row; a row with nothing to do has none.
        private IUiComponent IncludeCell(MediaImportRowV1 row)
        {
            var name = "Choices[" + row.Index.ToString(CultureInfo.InvariantCulture) + "].";
            var parts = new List<IUiComponent> { Hidden(name + "Index", row.Index.ToString(CultureInfo.InvariantCulture)) };
            if (CanInclude(row))
            {
                var include = new Toggle("import.row.include", row.IsIncluded);
                include.Control.Attribute("name", name + "Include").Attribute("data-import-row", "");

                // The first value wins when the switch is on.
                parts.Add(new Div(include).Class("media-import__include"));
                parts.Add(Hidden(name + "Include", "false"));
            }

            return new Div([.. parts]);
        }

        // Something may be done with the row: a new title, a title to monitor, an open one that has a candidate.
        private static bool CanInclude(MediaImportRowV1 row) => row.StatusTypeId switch
        {
            MediaImportStatusType.New => true,
            MediaImportStatusType.InLibrary => !row.HasTarget && row.CanMonitor && row.Action != MediaImportActionType.AddOnly,
            MediaImportStatusType.Open => row.Candidates.Count > 0,
            _ => false
        };

        // The row may differ from the list in how it is added.
        private static bool CanChoose(MediaImportRowV1 row) => !(row.StatusTypeId == MediaImportStatusType.InLibrary && row.HasTarget) && row.CanMonitor && CanInclude(row) && row.MediaTypeId is not null;

        private IUiComponent TitleCell(MediaImportRowV1 row)
        {
            var kind = row.MediaTypeId is { } type ? texts[MediaTypeLabel.Key(type, row.MediaSubtypeId)] : row.TypeKey;
            var meta = string.Join(" · ", new[] { kind, row.ReleaseYear?.ToString(CultureInfo.CurrentCulture), row.Creator }.Where(text => !string.IsNullOrEmpty(text)));
            return new Div(new Span(new Icon(TypeIcon(row.TypeKey), assets, 18)).Class("media-import__tile"),
                new Div(new Strong(new Truncated(row.Title ?? row.MatchTitle ?? Identity(row.Choice) ?? "—")), new Span(new Truncated(meta))).Class("media-import__titles")).Class("media-import__title");
        }

        private IUiComponent StatusCell(MediaImportRowV1 row)
        {
            var chip = Chip(StatusChip(row.StatusTypeId), "import.status." + row.StatusTypeId);
            var detail = row.StatusTypeId switch
            {
                MediaImportStatusType.Duplicate => string.Format(CultureInfo.CurrentCulture, texts["import.duplicateOf"], (row.DuplicateOf ?? 0) + 1),
                _ when row.Problem is { } problem => texts["import.problem." + problem] + (row.ProblemDetail is { } value ? ": " + value : ""),
                _ => null
            };
            return detail is null ? chip : new Div(chip, new Span(new Truncated(detail)).Class("media-import__detail")).Class("media-import__status-cell");
        }

        // The title in the library, the provider entry it would come from, or the candidates to choose from.
        private IUiComponent MatchCell(MediaImportRowV1 row)
        {
            var name = "Choices[" + row.Index.ToString(CultureInfo.InvariantCulture) + "].";
            if (row.StatusTypeId == MediaImportStatusType.InLibrary)
            {
                return new Div(new A(new ValueText(WithYear(row.MatchTitle, row.MatchYear))).Attribute("href", "/Admin/Works/" + row.WorkId!.Value.ToString(CultureInfo.InvariantCulture)),
                    new Span(new Text("admin.works.source." + row.MatchSource)).Class("media-import__detail")).Class("media-import__status-cell");
            }

            if (row.Candidates.Count == 0 && row.StatusTypeId != MediaImportStatusType.Open)
            {
                return row.StatusTypeId == MediaImportStatusType.New
                    ? new Div(new Span(new Truncated(WithYear(row.MatchTitle, row.MatchYear))), new Span(new Truncated(Identity(row.Choice) ?? "")).Class("media-import__detail")).Class("media-import__status-cell")
                    : new Nothing();
            }

            // A candidate is only proposed; the person's own choice is the one that counts.
            var posted = _posted.GetValueOrDefault(row.Index)?.Choice;
            var selected = posted ?? row.Choice ?? row.Candidates.FirstOrDefault(candidate => candidate.IsSuggested)?.Choice ?? "";
            var options = new List<IUiComponent> { Item("", texts["import.choose"], selected == "") };
            options.AddRange(row.Candidates.Select(candidate => Item(candidate.Choice, string.Join(" · ", new[] { WithYear(candidate.Title, candidate.ReleaseYear), candidate.Creator, texts["admin.works.source." + candidate.Source] }
                .Where(text => !string.IsNullOrEmpty(text))), candidate.Choice == selected)));
            var parts = new List<IUiComponent> { Field(new Select([.. options]).Attribute("name", name + "Choice").TextAttribute("aria-label", "import.choose")) };
            if (row.StatusTypeId == MediaImportStatusType.Open)
            {
                parts.Add(new Div(Field(new Input().Attribute("type", "text").Attribute("name", name + "Query").Attribute("maxlength", "200").Attribute("autocomplete", "off")
                        .Attribute("value", row.Query ?? "").TextAttribute("aria-label", "import.search").TextAttribute("placeholder", "import.search")),
                    new Button(new Text("import.search.go")).Compact().Submit()).Class("media-import__search"));
            }

            return new Div([.. parts]).Class("media-import__stack");
        }

        // How a title is added: the list's way unless the row says another.
        private IUiComponent ActionCell(MediaImportRowV1 row, MediaImportActionType action)
        {
            if (row.StatusTypeId == MediaImportStatusType.InLibrary && row.HasTarget) return new Text("import.intent.Tracked");
            if (!CanChoose(row)) return new Nothing();
            var posted = _posted.GetValueOrDefault(row.Index);
            var actions = new List<IUiComponent> { Item("", string.Format(CultureInfo.CurrentCulture, texts["import.defaultAction"], texts["import.action." + action]), posted?.Action is null) };
            actions.AddRange(Enum.GetValues<MediaImportActionType>().Select(value => Item(((byte)value).ToString(CultureInfo.InvariantCulture), texts["import.action." + value], posted?.Action == value)));
            return Field(new Select([.. actions]).Attribute("name", "Choices[" + row.Index.ToString(CultureInfo.InvariantCulture) + "].Action").TextAttribute("aria-label", "import.action"));
        }

        // A row's own quality profile or library; only a kind of title that has several to choose from offers them.
        private IUiComponent Picker(MediaImportRowV1 row, string field, string labelKey, string emptyKey, IEnumerable<MediaImportOptionV1> choices, long? selected)
        {
            var options = Fitting(choices, row).ToArray();
            if (!CanChoose(row) || options.Length < 2) return new Nothing();
            var items = new List<IUiComponent> { Item("", texts[emptyKey], selected is null) };
            items.AddRange(options.Select(option => Item(option.Id.ToString(CultureInfo.InvariantCulture), option.Name, option.Id == selected)));
            return Field(new Select([.. items]).Attribute("name", "Choices[" + row.Index.ToString(CultureInfo.InvariantCulture) + "]." + field).TextAttribute("aria-label", labelKey));
        }

        // A field in the one look of the forms.
        private static IUiComponent Field(HtmlElement control) => new Div(control).Class("ui-field__control media-import__field");

        // A kind of title has something to choose when it has more than one quality profile or library.
        private static bool HasChoice(MediaImportPreviewV1 listing, MediaImportRowV1 kind) => Fitting(listing.Profiles, kind).Count() > 1 || Fitting(listing.Libraries, kind).Count() > 1;

        private static IEnumerable<MediaImportOptionV1> Fitting(IEnumerable<MediaImportOptionV1> options, MediaImportRowV1 row) =>
            options.Where(option => option.MediaTypeId == row.MediaTypeId && (option.MediaSubtypeId is null || option.MediaSubtypeId == row.MediaSubtypeId));

        private SelectOption[] Options(IEnumerable<MediaImportOptionV1> options, string emptyKey) =>
            [new SelectOption("", texts[emptyKey]), .. options.Select(option => new SelectOption(option.Id.ToString(CultureInfo.InvariantCulture), option.Name))];

        private SelectOption[] ActionOptions() =>
            [.. Enum.GetValues<MediaImportActionType>().Select(value => new SelectOption(((byte)value).ToString(CultureInfo.InvariantCulture), texts["import.action." + value]))];

        // A provider entry as its source and id, such as "TMDB 603".
        private string? Identity(string? choice) =>
            choice?.IndexOf(':') is > 0 and var colon ? texts["admin.works.source." + choice[..colon]] + " " + choice[(colon + 1)..] : null;

        private static string WithYear(string? title, int? year) => (title ?? "") + (year is { } value ? " (" + value.ToString(CultureInfo.CurrentCulture) + ")" : "");

        private static Input Hidden(string name, string value) => (Input)new Input().Attribute("type", "hidden").Attribute("name", name).Attribute("value", value);

        private static HtmlElement Item(string value, string label, bool selected)
        {
            var option = new OptionElement(new ValueText(label));
            option.Attribute("value", value);
            if (selected) option.Attribute("selected", "selected");
            return option;
        }

        public IActionResult OnGet()
        {
            if (!texts.CanImportMedia()) return Forbid();
            PreparePage();
            return Page();
        }

        public IActionResult OnGetExample()
        {
            if (!texts.CanImportMedia()) return Forbid();
            return File(Encoding.UTF8.GetBytes(MediaImportExample.Json), "application/json; charset=utf-8", MediaImportExample.FileName);
        }

        public async Task<IActionResult> OnPostPreviewAsync(IFormFile? file, string? json, MediaImportActionType? action, List<DefaultRow>? defaults, List<ChoiceRow>? choices)
        {
            Read(file, json, action, defaults, choices);
            _preview = await preview.ExecuteAsync<MediaImportPreviewV1>(_input!, HttpContext.RequestAborted);
            PreparePage();
            return Page();
        }

        public async Task<IActionResult> OnPostApplyAsync(IFormFile? file, string? json, MediaImportActionType? action, List<DefaultRow>? defaults, List<ChoiceRow>? choices)
        {
            Read(file, json, action, defaults, choices);
            _result = await run.ExecuteAsync<MediaImportResultV1>(_input!, HttpContext.RequestAborted);
            PreparePage();
            return Page();
        }

        private void Read(IFormFile? file, string? json, MediaImportActionType? action, List<DefaultRow>? defaults, List<ChoiceRow>? choices)
        {
            _input = new MediaImportDataV1(file is { Length: > 0 } ? new UploadedFile(file.FileName, file.Length, file.OpenReadStream) : null, json, action ?? MediaImportActionType.MonitorAndSearch,
                new((defaults ?? []).Where(row => row.Key is not null).Select(row => new MediaImportDefaultV1(row.Key!, row.AcquisitionProfileId, row.LibraryRootId))),
                new((choices ?? []).Where(row => row.Index is not null).Select(row => new MediaImportChoiceV1(row.Index!.Value, row.Include, row.Choice, row.Query, row.Action, row.AcquisitionProfileId, row.LibraryRootId))));
            _posted = _input.Choices.ToDictionary(choice => choice.Index);
        }

        public sealed record DefaultRow(string? Key, long? AcquisitionProfileId, long? LibraryRootId);
        public sealed record ChoiceRow(int? Index, bool? Include, string? Choice, string? Query, MediaImportActionType? Action, long? AcquisitionProfileId, long? LibraryRootId);

        // An empty cell: on a phone it leaves out its label too.
        private sealed class Nothing : IUiComponent
        {
            public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
            {
            }
        }

        private sealed class Select(params IUiComponent[] children) : HtmlElement("select", children);
        private sealed class OptionElement(params IUiComponent[] children) : HtmlElement("option", children);
    }
}
