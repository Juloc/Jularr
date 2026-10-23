using System.Globalization;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin.Acquisition
{
    public sealed partial class AcquisitionModel
    {
        private IUiComponent DownloadClientTabs(long connectionId)
        {
            var tabs = new[] { ("downloads", "console.downloads"), ("history", "console.history"), ("files", "console.files") };
            return new NavigationTabs([.. tabs.Select(tab => new NavigationTab(tab.Item2,
                Address(connectionId) + "&Pane=" + tab.Item1, Pane == tab.Item1))], "nav.tabs");
        }

        private IUiComponent DownloadClientPane(long id, string? csrf)
        {
            var data = _downloadDashboard;
            if (data is null) return new P(new Text("console.notMapped"));
            return new Div(Pane switch
            {
                "history" => DownloadHistory(data, id, csrf),
                "files" => DownloadFiles(id, csrf),
                _ => DownloadQueue(data, id, csrf)
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


        private IUiComponent DownloadFiles(long connectionId, string? csrf)
        {
            var area = Area is "incomplete" ? "incomplete" : "complete";
            var urls = new Div(
                new A(new Text("console.complete")).Class(area == "complete" ? "button" : "button button--soft")
                    .Attribute("href", FileAddress(connectionId, "complete")),
                new A(new Text("console.incomplete")).Class(area == "incomplete" ? "button" : "button button--soft")
                    .Attribute("href", FileAddress(connectionId, "incomplete"))).Class("download-console__actions");

            var body = new List<IUiComponent> { urls };
            if (_fileBrowser is not { } folder)
            {
                body.Add(new P(new Text("console.notMapped")));
                return new Section([.. body]).Class("download-console__section");
            }

            body.Add(new P(new ValueText(folder.Root + (folder.RelativePath.Length > 0 ? "/" + folder.RelativePath : ""))).Class("download-console__note"));
            if (folder.RelativePath.Length > 0)
            {
                var up = folder.RelativePath.LastIndexOf('/');
                body.Add(new A(new ValueText("← ..")).Class("button button--compact button--soft")
                    .Attribute("href", FileAddress(connectionId, area, up < 0 ? "" : folder.RelativePath[..up])));
            }
            var rows = new Table<DownloadClientFileV1>(folder.Entries)
                .Content("console.name", entry => entry.IsDirectory
                    ? new A(new ValueText("📁 " + entry.Name)).Attribute("href", FileAddress(connectionId, area, entry.RelativePath))
                    : new Span(new ValueText(entry.Name)))
                .Content("console.size", entry => new ValueText(entry.SizeBytes is { } size ? ByteSize.Format(size) : "—"))
                .Content("console.fileAssignment", entry => new ValueText(entry.Assignment))
                .Content("console.actions", entry => entry.Assignment is "orphan" or "unassigned"
                    ? new A(new Text("console.cleanup")).Class("button button--compact button--warn")
                        .Attribute("href", FileAddress(connectionId, area, folder.RelativePath, FilePage) + "&DeleteArea=" + area
                            + "&DeletePath=" + Uri.EscapeDataString(entry.RelativePath))
                    : new Span());
            body.Add(folder.Entries.Count == 0 ? new P(new Text("console.filesEmpty")) : rows);
            if (folder.HasMore || FilePage > 1)
            {
                body.Add(new Div(
                    FilePage > 1 ? new A(new ValueText("←")).Class("button button--compact")
                        .Attribute("href", FileAddress(connectionId, area, folder.RelativePath, FilePage - 1)) : new Span(),
                    folder.HasMore ? new A(new ValueText("→")).Class("button button--compact")
                        .Attribute("href", FileAddress(connectionId, area, folder.RelativePath, FilePage + 1)) : new Span()).Class("download-console__actions"));
            }
            if (_deletePreview is { } preview)
            {
                body.Add(new Div(
                    new P(new ValueText("Löschen bestätigen: " + preview.RelativePath + " · " + preview.Files.ToString(CultureInfo.CurrentCulture)
                        + " Dateien · " + ByteSize.Format(preview.Bytes))),
                    new Form<DownloadClientFolderRequestV1>(form =>
                    {
                        form.Hidden(item => item.Area);
                        form.Hidden(item => item.RelativePath);
                        form.Hidden(item => item.PageNumber);
                        form.Hidden(item => item.Confirmed);
                        form.Hidden(item => item.Fingerprint);
                        form.Submit("console.confirm");
                    }, Address(connectionId, handler: "DeleteFile"), csrf,
                        new DownloadClientFolderRequestV1(preview.Area, preview.RelativePath, 1, true, preview.Fingerprint)),
                    new A(new Text("common.back")).Class("button button--compact")
                        .Attribute("href", FileAddress(connectionId, area, folder.RelativePath, FilePage))).Class("download-console__confirm"));
            }
            body.Add(new P(new Text("console.filesWarning")).Class("download-console__note"));
            return new Section([.. body]).Class("download-console__section");
        }

        private string FileAddress(long id, string area, string? path = null, int page = 1) =>
            Address(id) + "&Pane=files&Area=" + Uri.EscapeDataString(area) + "&FolderPath=" + Uri.EscapeDataString(path ?? "")
                + "&FilePage=" + page.ToString(CultureInfo.InvariantCulture);

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
