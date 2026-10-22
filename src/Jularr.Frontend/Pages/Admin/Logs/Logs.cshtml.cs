using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Data.Enums.Instance;
using Jularr.Backend.Service.Admin.System.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Logs
{
    // The messages of the system, newest first: what happened to the media, background jobs that failed and, with Diagnostics, the application's own warnings and errors.
    public sealed class LogsModel(ReadLogsV1 read, UiText texts, FrontendAssets assets) : JularrPage
    {
        private PageResult<LogEntryV1>? _logs;

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public LogLevelType? Level { get; set; }

        [BindProperty(SupportsGet = true)]
        public LogSourceType? Source { get; set; }

        [BindProperty(SupportsGet = true, Name = "q")]
        public string? Query { get; set; }

        protected override void Define(Page page)
        {
            page.Title("admin.logs.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Logs/Logs.entry.scss");
            page.Asset("Framework/Components/SelectLinkMenu.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");
            if (_logs is not { } logs) return;

            // One row: the level as chips, the source as a menu, and the search.
            var levels = new[] { (LogLevelType?)null, LogLevelType.Error, LogLevelType.Warning, LogLevelType.Info };
            var sources = new List<LogSourceType?> { null, LogSourceType.Activity, LogSourceType.Job };
            if (texts.CanDiagnose()) sources.Add(LogSourceType.Application);
            var kept = new Dictionary<string, string>();
            if (Level is { } chosenLevel) kept["level"] = chosenLevel.ToString();
            if (Source is { } chosenSource) kept["source"] = chosenSource.ToString();
            var filters = new Div(
                new NavigationTabs([.. levels.Select(item => new NavigationTab("logs.level." + (item?.ToString() ?? "All"), Address(item, Source, Query, 1), item == Level))], "admin.logs.title", "logs"),
                new Div(new SelectLinkMenu(assets, [.. sources.Select(item => new NavigationTab("logs.source." + (item?.ToString() ?? "All"), Address(Level, item, Query, 1), item == Source))], "logs.source"),
                    new SearchBox("/Admin/Logs", "logs.search", Query, kept)).Class("logs-filters__tools")).Class("logs-filters");

            var rows = logs.Items.Count > 0 ? new Div([.. logs.Items.Select(Row)]).Class("logs-list") : (IUiComponent)new P(new Text("logs.empty"));
            page.Content.Add(new Section(filters, rows.Paged(logs.Items.Count)).Class("ui-section logs"));
        }

        private IUiComponent Row(LogEntryV1 entry)
        {
            var tone = entry.Level switch
            {
                LogLevelType.Error => UiTone.Danger,
                LogLevelType.Warning => UiTone.Warning,
                _ => UiTone.Info
            };
            var (origin, message) = entry.Source switch
            {
                LogSourceType.Activity => (texts["history.event." + entry.Origin], entry.Message),
                LogSourceType.Job => (texts["jobs.job." + entry.Origin], ErrorText(entry.Message)),
                _ => (entry.Origin, entry.Message)
            };
            return new Div(new Span(new ValueText(entry.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))).Class("logs-list__when"), new StateChip(new Text("logs.level." + entry.Level), tone),
                new Div(new Truncated(message), new Div(new Truncated(origin)).Class("logs-list__origin")).Class("logs-list__message")).Class("logs-list__row");
        }

        // A job's message is the error it failed with.
        private string ErrorText(string key) => texts.Has("errors." + key) ? texts["errors." + key] : key;

        private static string Address(LogLevelType? level, LogSourceType? source, string? query, int page)
        {
            var parts = new List<string>();
            if (level is { } l) parts.Add("Level=" + l);
            if (source is { } s) parts.Add("Source=" + s);
            if (!string.IsNullOrWhiteSpace(query)) parts.Add("q=" + Uri.EscapeDataString(query));
            if (page > 1) parts.Add("PageNumber=" + page.ToString(CultureInfo.InvariantCulture));
            return "/Admin/Logs" + (parts.Count > 0 ? "?" + string.Join('&', parts) : "");
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync() =>
            _logs = await read.ExecuteAsync<PageResult<LogEntryV1>>(new ReadLogsParametersV1(Level, Source, Query, Math.Max(1, PageNumber)), HttpContext.RequestAborted);
    }
}
