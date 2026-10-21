using System.Globalization;
using System.Text;
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
    public sealed class MediaImportModel(PreviewMediaImportV1 preview, RunMediaImportV1 run, IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string Address = "/Admin/Import";
        private MediaImportDataV1? _input;
        private MediaImportPreviewV1? _preview;
        private MediaImportResultV1? _result;
        private Dictionary<int, MediaImportChoiceV1> _posted = [];

        protected override void Define(Page page)
        {
            page.Title("admin.import.title");
            page.Parent("admin.works.title", "/Admin/Works");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/MediaImport/MediaImport.entry.scss");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken!;
            var content = new List<IUiComponent>();
            if (_result is { } result) Result(content, result, csrf);
            else if (_preview is { } listing) Preview(content, listing, csrf);
            else Upload(content, csrf);
            page.Content.Add(new Div([.. content]).Class("media-import"));
        }

        private void Upload(List<IUiComponent> content, string csrf)
        {
            content.Add(new Div(new A(new Text("import.example")).Class("button").Attribute("href", Address + "?handler=Example").Attribute("download", MediaImportExample.FileName)).Class("media-import__summary"));
            content.Add(new Form<MediaImportDataV1>(form =>
            {
                form.File(item => item.File, "import.file");
                form.Multiline(item => item.Json, "import.json");
                form.Select(item => item.Action, "import.action", ActionOptions());
                form.Submit("import.preview");
            }, Address + "?handler=Preview", csrf, _input ?? new MediaImportDataV1(null, null, MediaImportActionType.MonitorAndSearch), FormError));
        }

        private void Preview(List<IUiComponent> content, MediaImportPreviewV1 listing, string csrf)
        {
            var rows = listing.Rows;
            var summary = Enum.GetValues<MediaImportStatusType>().Select(status => (Status: status, Count: rows.Count(row => row.StatusTypeId == status))).Where(item => item.Count > 0)
                .Select(item => (IUiComponent)new Tag(new Strong(new ValueText(item.Count.ToString(CultureInfo.CurrentCulture))), new Text("import.status." + item.Status))).ToList();
            if (listing.Unsearched > 0) summary.Add(new Tag(new Strong(new ValueText(listing.Unsearched.ToString(CultureInfo.CurrentCulture))), new Text("import.unsearched")));
            summary.Add(new A(new Text("import.restart")).Class("button").Attribute("href", Address));
            content.Add(new Div([.. summary]).Class("media-import__summary"));

            // The defaults of each kind of title the list holds.
            var action = _input?.Action ?? MediaImportActionType.MonitorAndSearch;
            var kinds = rows.Where(row => row.MediaTypeId is not null).GroupBy(row => row.TypeKey, StringComparer.Ordinal).Select(group => group.First()).ToDictionary(row => row.TypeKey, StringComparer.Ordinal);
            var defaults = new BaseDataList<MediaImportDefaultV1>(kinds.Keys.Select(key => _input?.Defaults.FirstOrDefault(item => item.Key == key) ?? new MediaImportDefaultV1(key, null, null)));
            content.Add(new Form<MediaImportDataV1>(form =>
            {
                form.Hidden(item => item.Json);
                form.Select(item => item.Action, "import.action", ActionOptions());
                if (kinds.Keys.Any(key => HasChoice(listing, kinds[key])))
                {
                    form.Section("import.defaults", section => section.Rows(item => item.Defaults, (row, item) =>
                    {
                        var kind = kinds[item.Key];
                        row.Hidden(value => value.Key);
                        row.Content(new Strong(new Text(MediaTypeLabel.Key(kind.MediaTypeId!.Value, kind.MediaSubtypeId))));
                        if (Fitting(listing.Profiles, kind).Count() > 1) row.Select(value => value.AcquisitionProfileId, "import.profile", Options(Fitting(listing.Profiles, kind), "import.defaultProfile"));
                        if (Fitting(listing.Libraries, kind).Count() > 1) row.Select(value => value.LibraryRootId, "import.library", Options(Fitting(listing.Libraries, kind), "import.defaultLibrary"));
                    }, item => kinds.TryGetValue(item.Key, out var kind) && HasChoice(listing, kind)));
                }

                form.Content(new Div(new Table<MediaImportRowV1>(rows)
                    .Content("import.col.include", row => IncludeCell(row, action))
                    .Content("media.title", TitleCell)
                    .Content("table.status", StatusCell)
                    .Content("import.col.match", MatchCell)
                    .Content("import.col.options", row => OptionsCell(row, listing, action))).Class("ui-field--wide media-import__rows"));
                form.Submit(new Button(new Text(listing.Unsearched > 0 ? "import.searchMore" : "import.refresh")));
                var apply = new Button(new Text("import.apply")).Primary();
                apply.Attribute("formaction", Address + "?handler=Apply");
                form.Submit(apply);
            }, Address + "?handler=Preview", csrf, new MediaImportDataV1(null, listing.Json, action, defaults), FormError));
        }

        private void Result(List<IUiComponent> content, MediaImportResultV1 result, string csrf)
        {
            var rows = result.Rows;
            var summary = Enum.GetValues<MediaImportOutcomeType>().Select(outcome => (Outcome: outcome, Count: rows.Count(row => row.OutcomeTypeId == outcome))).Where(item => item.Count > 0)
                .Select(item => (IUiComponent)new Tag(new Strong(new ValueText(item.Count.ToString(CultureInfo.CurrentCulture))), new Text("import.outcome." + item.Outcome))).ToList();
            if (result.RemainingJson is { } remaining)
            {
                summary.Add(new PostForm(Address + "?handler=Preview", csrf, [("json", remaining), ("action", ((byte)(_input?.Action ?? MediaImportActionType.MonitorAndSearch)).ToString(CultureInfo.InvariantCulture))],
                    new Button(new Text("import.continue")).Primary().Submit()));
            }

            summary.Add(new A(new Text("import.again")).Class("button").Attribute("href", Address));
            content.Add(new Div([.. summary]).Class("media-import__summary"));
            var shown = rows.Where(row => row.OutcomeTypeId != MediaImportOutcomeType.Skipped).ToArray();
            content.Add(new Table<MediaImportResultRowV1>(shown)
                .Content("media.title", row => new Span(new ValueText(row.Title ?? "—")))
                .Content("table.status", row => row.Searches
                    ? new Div(new Tag(new Text("import.outcome." + row.OutcomeTypeId)), new Tag(new Text("import.searches")))
                    : new Tag(new Text("import.outcome." + row.OutcomeTypeId)))
                .Content("common.actions", row => row.WorkId is { } id
                    ? new A(new Text("common.open")).Attribute("href", "/Admin/Works/" + id.ToString(CultureInfo.InvariantCulture))
                    : new Span()));
        }

        private IUiComponent IncludeCell(MediaImportRowV1 row, MediaImportActionType action)
        {
            var name = "Choices[" + row.Index.ToString(CultureInfo.InvariantCulture) + "].";
            var parts = new List<IUiComponent> { Hidden(name + "Index", row.Index.ToString(CultureInfo.InvariantCulture)) };
            if (CanInclude(row))
            {
                var box = new Input().Class("media-import__check").Attribute("type", "checkbox").Attribute("name", name + "Include").Attribute("value", "true").TextAttribute("aria-label", "import.row.include");
                if (row.IsIncluded) box.Attribute("checked", "checked");

                // The first value wins when the box is checked.
                parts.Add(box);
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

        private IUiComponent TitleCell(MediaImportRowV1 row)
        {
            var kind = row.MediaTypeId is { } type ? texts[MediaTypeLabel.Key(type, row.MediaSubtypeId)] : row.TypeKey;
            var meta = string.Join(" · ", new[] { kind, row.ReleaseYear?.ToString(CultureInfo.CurrentCulture), row.Creator }.Where(text => !string.IsNullOrEmpty(text)));
            return new Div(new Strong(new ValueText(row.Title ?? row.MatchTitle ?? Identity(row.Choice) ?? "—")), new Span(new ValueText(meta))).Class("media-import__title");
        }

        private IUiComponent StatusCell(MediaImportRowV1 row)
        {
            var tag = new Tag(new Text("import.status." + row.StatusTypeId));
            var detail = row.StatusTypeId switch
            {
                MediaImportStatusType.Duplicate => string.Format(CultureInfo.CurrentCulture, texts["import.duplicateOf"], (row.DuplicateOf ?? 0) + 1),
                _ when row.Problem is { } problem => texts["import.problem." + problem] + (row.ProblemDetail is { } value ? ": " + value : ""),
                _ => null
            };
            return detail is null ? tag : new Div(tag, new Span(new ValueText(detail)).Class("media-import__detail"));
        }

        // The title in the library, the provider entry it would come from, or the candidates to choose from.
        private IUiComponent MatchCell(MediaImportRowV1 row)
        {
            var name = "Choices[" + row.Index.ToString(CultureInfo.InvariantCulture) + "].";
            if (row.StatusTypeId == MediaImportStatusType.InLibrary)
            {
                return new Div(new A(new ValueText(WithYear(row.MatchTitle, row.MatchYear))).Attribute("href", "/Admin/Works/" + row.WorkId!.Value.ToString(CultureInfo.InvariantCulture)),
                    new Span(new Text("admin.works.source." + row.MatchSource)).Class("media-import__detail"));
            }

            if (row.Candidates.Count == 0 && row.StatusTypeId != MediaImportStatusType.Open)
            {
                return row.StatusTypeId == MediaImportStatusType.New
                    ? new Div(new ValueText(WithYear(row.MatchTitle, row.MatchYear)), new Span(new ValueText(Identity(row.Choice) ?? "")).Class("media-import__detail"))
                    : new Span();
            }

            // A candidate is only proposed; the person's own choice is the one that counts.
            var posted = _posted.GetValueOrDefault(row.Index)?.Choice;
            var selected = posted ?? row.Choice ?? row.Candidates.FirstOrDefault(candidate => candidate.IsSuggested)?.Choice ?? "";
            var options = new List<IUiComponent> { Item("", texts["import.choose"], selected == "") };
            options.AddRange(row.Candidates.Select(candidate => Item(candidate.Choice, string.Join(" · ", new[] { WithYear(candidate.Title, candidate.ReleaseYear), candidate.Creator, texts["admin.works.source." + candidate.Source] }
                .Where(text => !string.IsNullOrEmpty(text))), candidate.Choice == selected)));
            var parts = new List<IUiComponent> { new Select([.. options]).Class("media-import__select").Attribute("name", name + "Choice").TextAttribute("aria-label", "import.choose") };
            if (row.StatusTypeId == MediaImportStatusType.Open)
            {
                parts.Add(new Div(new Input().Class("media-import__input").Attribute("type", "text").Attribute("name", name + "Query").Attribute("maxlength", "200").Attribute("autocomplete", "off")
                        .Attribute("value", row.Query ?? "").TextAttribute("aria-label", "import.search").TextAttribute("placeholder", "import.search"),
                    new Button(new Text("import.search.go")).Compact().Submit()).Class("media-import__search"));
            }

            return new Div([.. parts]).Class("media-import__stack");
        }

        // How a title is added: the list's way unless the row says another, with its own quality profile and library.
        private IUiComponent OptionsCell(MediaImportRowV1 row, MediaImportPreviewV1 listing, MediaImportActionType action)
        {
            if (row.StatusTypeId == MediaImportStatusType.InLibrary && row.HasTarget) return new Text("import.intent.Tracked");
            if (!row.CanMonitor || !CanInclude(row) || row.MediaTypeId is null) return new Span();
            var name = "Choices[" + row.Index.ToString(CultureInfo.InvariantCulture) + "].";
            var posted = _posted.GetValueOrDefault(row.Index);
            var actions = new List<IUiComponent> { Item("", string.Format(CultureInfo.CurrentCulture, texts["import.defaultAction"], texts["import.action." + action]), posted?.Action is null) };
            actions.AddRange(Enum.GetValues<MediaImportActionType>().Select(value => Item(((byte)value).ToString(CultureInfo.InvariantCulture), texts["import.action." + value], posted?.Action == value)));
            return new Div(
                new Select([.. actions]).Class("media-import__select").Attribute("name", name + "Action").TextAttribute("aria-label", "import.action"),
                Picker(name + "AcquisitionProfileId", "import.profile", "import.defaultProfile", Fitting(listing.Profiles, row), posted?.AcquisitionProfileId),
                Picker(name + "LibraryRootId", "import.library", "import.defaultLibrary", Fitting(listing.Libraries, row), posted?.LibraryRootId)).Class("media-import__stack");
        }

        // Only a kind of title that has several to choose from offers them.
        private IUiComponent Picker(string name, string labelKey, string emptyKey, IEnumerable<MediaImportOptionV1> options, long? selected)
        {
            if (options.Count() < 2) return new Span();
            var items = new List<IUiComponent> { Item("", texts[emptyKey], selected is null) };
            items.AddRange(options.Select(option => Item(option.Id.ToString(CultureInfo.InvariantCulture), option.Name, option.Id == selected)));
            return new Select([.. items]).Class("media-import__select").Attribute("name", name).TextAttribute("aria-label", labelKey);
        }

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

        private sealed class Select(params IUiComponent[] children) : HtmlElement("select", children);
        private sealed class OptionElement(params IUiComponent[] children) : HtmlElement("option", children);
    }
}
