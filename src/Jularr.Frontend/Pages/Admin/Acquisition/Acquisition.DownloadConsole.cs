using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin.Acquisition
{
    public sealed partial class AcquisitionModel
    {
        private IUiComponent DownloadCategoryForm(AcquisitionConnectionDetailsV1 connection, string? csrf)
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(connection.DownloadCategoryMap ?? "{}") ?? [];
            string? Read(string kind) => map.GetValueOrDefault(kind);

            var input = new DownloadClientCategoriesV1(Read("Movie"), Read("Tv"), Read("Anime"), Read("Book"), Read("Audiobook"),
                Read("Manga"), Read("LightNovel"), Read("Music"), Read("Game"));

            return new Form<DownloadClientCategoriesV1>(form =>
            {
                form.Section("console.mappingTitle", fields =>
                {
                    fields.Text(item => item.Movie, "console.kind.movie");
                    fields.Text(item => item.Tv, "console.kind.tv");
                    fields.Text(item => item.Anime, "console.kind.anime");
                    fields.Text(item => item.Book, "console.kind.book");
                    fields.Text(item => item.Audiobook, "console.kind.audiobook");
                    fields.Text(item => item.Manga, "console.kind.manga");
                    fields.Text(item => item.LightNovel, "console.kind.lightNovel");
                    fields.Text(item => item.Music, "console.kind.music");
                    fields.Text(item => item.Game, "console.kind.game");
                });
                form.Submit("common.save");
            }, Address(connection.Id, handler: "Categories"), csrf, input);
        }

        private IUiComponent DownloadClientTabs(long connectionId)
        {
            var tabs = new[]
            {
                ("general", "console.general"),
                ("downloads", "console.downloads"),
                ("statistics", "console.statistics"),
                ("history", "console.history"),
                ("files", "console.files")
            };
            var active = tabs.Any(tab => tab.Item1 == Pane) ? Pane : "general";
            return new NavigationTabs([.. tabs.Select(tab => new NavigationTab(tab.Item2,
                Address(connectionId) + "&Pane=" + tab.Item1, active == tab.Item1))], "nav.tabs");
        }

        private IUiComponent DownloadClientPane(long connectionId, string? csrf)
        {
            if (_downloadDashboard is not { } data) return new P(new Text("console.notMapped"));
            return new Div(Pane switch
            {
                "downloads" => DownloadQueue(data, connectionId, csrf),
                "statistics" => DownloadStatistics(data),
                "history" => DownloadHistory(data, connectionId, csrf),
                "files" => DownloadFiles(data, connectionId, csrf),
                _ => new P(new Text("console.notMapped"))
            }).Class("download-console");
        }

        private IUiComponent DownloadQueue(DownloadClientDashboardV1 data, long connectionId, string? csrf)
        {
            var summary = new Div(
                Metric("console.queueCount", data.Queue.Count.ToString(CultureInfo.CurrentCulture)),
                Metric("console.speed", data.SpeedBytesPerSecond is { } speed ? ByteSize.Format(speed) + "/s" : "—"),
                Metric("console.free", data.FreeBytes is { } free ? ByteSize.Format(free) : "—"),
                Metric("console.status", data.IsPaused ? texts["console.paused"] : texts["console.downloads"]))
                .Class("download-console__metrics");

            var rows = new Table<DownloadClientQueueItemV1>(data.Queue)
                .Text(item => item.Name, "console.name")
                .Content("console.progress", item => new Div(
                    new Progress().Attribute("max", "100").Attribute("value", (item.ProgressPercent ?? 0).ToString(CultureInfo.InvariantCulture))
                        .Attribute("aria-label", texts["console.progress"]),
                    new Span(new ValueText(item.ProgressPercent is { } percent ? percent.ToString(CultureInfo.CurrentCulture) + " %" : "—")))
                    .Class("download-console__progress"))
                .Content("console.size", item => new ValueText(item.SizeBytes is { } size ? ByteSize.Format(size) : "—"))
                .Content("console.timeLeft", item => new ValueText(item.TimeLeftSeconds is { } seconds ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : "—"))
                .Content("console.status", item => new Span(new ValueText(item.Status ?? "—")).Class("download-console__state"))
                .Content("console.actions", item => JobControls(connectionId, item.JobId, item.Status, csrf, false));

            return new Section(summary,
                new H2(new Text("console.queue")),
                data.Queue.Count == 0 ? new P(new Text("console.queueEmpty")) : rows,
                data.QueueLimited ? new P(new Text("console.limit")).Class("download-console__note") : new Span(),
                new A(new Text("console.refresh")).Attribute("href", Address(connectionId) + "&Pane=downloads").Class("button"))
                .Class("download-console__section");
        }

        private IUiComponent DownloadStatistics(DownloadClientDashboardV1 data)
        {
            var total = data.Queue.Where(job => job.SizeBytes is not null).Sum(job => job.SizeBytes ?? 0);
            return new Section(
                new Div(
                    Metric("console.speed", data.SpeedBytesPerSecond is { } speed ? ByteSize.Format(speed) + "/s" : "—"),
                    Metric("console.free", data.FreeBytes is { } free ? ByteSize.Format(free) : "—"),
                    Metric("console.queueCount", data.Queue.Count.ToString(CultureInfo.CurrentCulture)),
                    Metric("console.size", ByteSize.Format(total)),
                    Metric("console.today", data.TodayBytes is { } day ? ByteSize.Format(day) : "—"),
                    Metric("console.week", data.WeekBytes is { } week ? ByteSize.Format(week) : "—"),
                    Metric("console.month", data.MonthBytes is { } month ? ByteSize.Format(month) : "—"),
                    Metric("console.total", data.TotalBytes is { } all ? ByteSize.Format(all) : "—"))
                    .Class("download-console__metrics"),
                new H2(new Text("console.categories")),
                new P(new ValueText(data.Categories.Length > 0 ? string.Join(" · ", data.Categories) : "—")),
                new P(new Text("console.statsNote")).Class("download-console__note"))
                .Class("download-console__section");
        }

        private IUiComponent DownloadHistory(DownloadClientDashboardV1 data, long connectionId, string? csrf)
        {
            var rows = new Table<DownloadClientHistoryItemV1>(data.History)
                .Text(item => item.Name, "console.name")
                .Content("console.status", item => new Span(new ValueText(item.Status ?? "—"))
                    .Class(item.Failed ? "download-console__state download-console__state--error" : "download-console__state"))
                .Content("console.files", item => new ValueText(item.StoragePath ?? "—"))
                .Content("console.actions", item => JobControls(connectionId, item.JobId, item.Status, csrf, true));

            return new Section(new H2(new Text("console.history")),
                data.History.Count == 0 ? new P(new Text("console.historyEmpty")) : rows,
                data.HistoryLimited ? new P(new Text("console.limit")).Class("download-console__note") : new Span(),
                new A(new Text("console.refresh")).Attribute("href", Address(connectionId) + "&Pane=history").Class("button"))
                .Class("download-console__section");
        }

        private IUiComponent DownloadFiles(DownloadClientDashboardV1 data, long connectionId, string? csrf)
        {
            IUiComponent Folder(string name, string? path, bool available)
            {
                var files = data.Files.Where(entry => entry.Folder == name).ToArray();
                var table = new Table<DownloadClientFolderEntryV1>(files)
                    .Text(item => item.Name, "console.name")
                    .Content("console.size", item => new ValueText(item.SizeBytes is { } size ? ByteSize.Format(size) : "—"))
                    .Content("console.fileAssignment", item => new Span(new Text(item.Assignment switch
                    {
                        "orphan" => "console.orphan",
                        "queue" => "console.queued",
                        "history" => "console.inHistory",
                        "unchecked" => "console.unchecked",
                        _ => "console.unassigned"
                    })).Class(item.Assignment == "unassigned" ? "download-console__state download-console__state--warning" : "download-console__state"))
                    .Content("console.actions", item => item.Assignment == "orphan" ? OrphanActions(connectionId, item.Name, csrf) : new Span());

                return new Section(new H2(new Text(name == "complete" ? "console.complete" : "console.incomplete")),
                    new P(new ValueText(path ?? "—")).Class("download-console__note"),
                    !available ? new P(new Text("console.notMapped"))
                        : files.Length == 0 ? new P(new Text("console.filesEmpty")) : table)
                    .Class("download-console__folder");
            }

            return new Section(
                new P(new Text("console.filesNote")).Class("download-console__note"),
                new Div(Folder("complete", data.CompletePath, data.CompleteAvailable),
                    Folder("incomplete", data.IncompletePath, data.IncompleteAvailable)).Class("download-console__folders"))
                .Class("download-console__section");
        }

        private IUiComponent OrphanActions(long connectionId, string folderName, string? csrf)
        {
            if (ConfirmJobId == folderName)
            {
                return new Div(new P(new Text("console.removeOrphanConfirm")),
                    JobForm(connectionId, folderName, "removeOrphan", "console.confirm", csrf, true),
                    new A(new Text("common.back")).Attribute("href", Address(connectionId) + "&Pane=files").Class("button"))
                    .Class("download-console__confirm");
            }

            return new A(new Text("console.removeOrphan")).Class("button")
                .Attribute("href", Address(connectionId) + "&Pane=files&ConfirmJobId=" + Uri.EscapeDataString(folderName));
        }

        private IUiComponent JobControls(long connectionId, string jobId, string? status, string? csrf, bool history)
        {
            var removing = history ? "removeHistory" : "cancel";
            var confirm = ConfirmJobId == jobId;
            if (confirm)
            {
                return new Div(new P(new Text(history ? "console.removeHistoryConfirm" : "console.cancelConfirm")),
                    JobForm(connectionId, jobId, removing, "console.confirm", csrf, true),
                    new A(new Text("common.back")).Attribute("href", Address(connectionId) + "&Pane=" + (history ? "history" : "downloads")).Class("button"))
                    .Class("download-console__confirm");
            }

            if (history)
            {
                return new A(new Text("console.removeHistory")).Class("button")
                    .Attribute("href", Address(connectionId) + "&Pane=history&ConfirmJobId=" + Uri.EscapeDataString(jobId));
            }

            var paused = string.Equals(status, "Paused", StringComparison.OrdinalIgnoreCase);
            return new Div(JobForm(connectionId, jobId, paused ? "resume" : "pause", paused ? "console.resume" : "console.pause", csrf, false),
                new A(new Text("console.cancel")).Class("button")
                    .Attribute("href", Address(connectionId) + "&Pane=downloads&ConfirmJobId=" + Uri.EscapeDataString(jobId)))
                .Class("download-console__actions");
        }

        private IUiComponent JobForm(long connectionId, string jobId, string action, string label, string? csrf, bool confirmed) =>
            new Form<DownloadClientJobCommandV1>(form =>
            {
                form.Hidden(item => item.JobId);
                form.Hidden(item => item.Action);
                form.Hidden(item => item.Confirmed);
                form.Submit(new Button(new Text(label)).Compact());
            }, Address(connectionId, handler: "Job") + "&Pane=" + (action == "removeHistory" ? "history" : action == "removeOrphan" ? "files" : "downloads"),
                csrf, new DownloadClientJobCommandV1(jobId, action, confirmed));

        private static IUiComponent Metric(string key, string value) =>
            new Div(new Span(new Text(key)), new Strong(new ValueText(value))).Class("download-console__metric");
    }
}
