using System.Globalization;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Live.V1;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.User.Live.V1;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Modules.Live
{
    // What one page element shows: its phase and step for the styles, the percent for a bar and ready texts in the reader's language.
    internal sealed record LiveEntity(string Phase, int? Step, int? Percent, IReadOnlyDictionary<string, string> Text);

    // The service results of a stream as the entities the pages mark. A person's results hold no release, indexer, client or error, so none can appear.
    internal static class LiveEntities
    {
        public static LiveEntity Request(UiText texts, LiveRequestV1 request)
        {
            var phase = request.AcquisitionRequestStatusTypeId;
            return new LiveEntity(LiveFacts.Marker(phase), LiveFacts.Step(phase), request.ProgressPercent,
                Texts(("phase", LiveFacts.Phase(texts, phase, request.ProgressPercent)), ("facts", LiveFacts.Join(LiveFacts.Progress(request.ProgressPercent, null, null), LiveFacts.EtaLeft(texts, request.EtaSeconds)))));
        }

        public static LiveEntity AdminRequest(UiText texts, LiveAdminRequestV1 request)
        {
            var phase = request.AcquisitionRequestStatusTypeId;
            return new LiveEntity(LiveFacts.Marker(phase), LiveFacts.Step(phase), request.ProgressPercent,
                Texts(("phase", LiveFacts.Phase(texts, phase, request.ProgressPercent, admin: true)), ("facts", LiveFacts.Join(LiveFacts.Speed(request.BytesPerSecond), LiveFacts.Eta(request.EtaSeconds))),
                    ("release", request.ReleaseTitle), ("source", LiveFacts.Join(request.IndexerName, request.ClientName)),
                    ("error", phase == AcquisitionRequestStatusType.Completed ? null : Reason(texts, OperationType.Download, request.ReasonKey))));
        }

        public static LiveEntity Operation(UiText texts, LiveOperationV1 operation)
        {
            // A waiting or failed entry says why; a running one how far it is.
            var says = operation.ReasonKey is not null && (operation.IsWaiting || operation.OperationStatusTypeId == OperationStatusType.Failed);
            var running = operation.OperationStatusTypeId is OperationStatusType.Queued or OperationStatusType.Running;
            var phase = operation.IsWaiting ? "waiting" : (running && operation.DownloadStateTypeId is { } state ? state.ToString() : operation.OperationStatusTypeId.ToString()).ToLowerInvariant();
            var progress = says ? null : operation.ProgressPercent;
            return new LiveEntity(phase, null, progress, Texts(("status", OperationStatus(texts, operation.IsWaiting, operation.OperationStatusTypeId, says || !running ? null : operation.DownloadStateTypeId)),
                ("reason", says ? Reason(texts, operation.OperationTypeId, operation.ReasonKey) : null),
                ("speed", says ? null : OperationSpeed(texts, operation.OperationStatusTypeId, operation.DownloadStateTypeId, operation.BytesPerSecond)),
                ("detail", says ? null : OperationDetail(progress, operation.SizeBytes, operation.RemainingBytes, operation.EtaSeconds)),
                ("percent", Percent(progress)), ("eta", says ? null : LiveFacts.Eta(operation.EtaSeconds))));
        }

        // The queue's tiles: how many run, the pace, how many import and how many failed.
        public static LiveEntity Queue(ActivitySummaryV1 queue) =>
            new("queue", null, null, Texts(("active", Count(queue.ActiveCount)), ("speed", LiveFacts.Speed(queue.BytesPerSecond)), ("import", Count(queue.ImportCount)),
                ("errors", Count(queue.ErrorCount))));

        // The figures of the Downloads page for All or one client: speed, what is left and how long, what runs and what waits, and the speed line.
        public static LiveEntity Downloads(UiText texts, IReadOnlyList<DownloadClientV1> clients)
        {
            var speed = clients.Sum(client => client.BytesPerSecond);
            var remaining = clients.Sum(client => client.RemainingBytes);
            var active = clients.Sum(client => client.ActiveCount);
            var waiting = Math.Max(0, clients.Sum(client => client.QueueCount) - active);
            return new LiveEntity("downloads", null, null, Texts(("speed", LiveFacts.Speed(speed)), ("remaining", ByteSize.Format(remaining)),
                ("eta", speed > 0 && remaining > 0 ? string.Format(CultureInfo.CurrentCulture, texts["downloads.etaIn"], LiveFacts.Eta((int)Math.Min(int.MaxValue, remaining / speed))) : null),
                ("active", Count(active)), ("waiting", string.Format(CultureInfo.CurrentCulture, texts["downloads.waiting"], waiting)),
                ("line", Sparkline.Points([.. SpeedLine(clients).Select(value => (double)value)], height: SpeedLineHeight))));
        }

        public const int SpeedLineHeight = 60;

        // The clients' lines added up, their newest samples aligned.
        public static long[] SpeedLine(IReadOnlyList<DownloadClientV1> clients)
        {
            var length = clients.Count == 0 ? 0 : clients.Max(client => client.SpeedSamples.Length);
            var line = new long[length];
            foreach (var client in clients)
            {
                for (var index = 0; index < client.SpeedSamples.Length; index++) line[length - client.SpeedSamples.Length + index] += client.SpeedSamples[index];
            }

            return line;
        }

        // A news server's last pass: connections in use of those allowed, its pace and the share of missing articles.
        public static LiveEntity NewsServer(UiText texts, NewsServerV1 server) =>
            new("news-server", null, null, Texts(("connections", string.Format(CultureInfo.CurrentCulture, texts["downloads.connectionsOf"], server.Connections, server.MaxConnections)),
                ("speed", LiveFacts.Speed(server.BytesPerSecond)),
                ("missing", server.Articles > 0 ? (server.MissingArticles * 100d / server.Articles).ToString("0.##", CultureInfo.CurrentCulture) + "%" : "–")));

        // The figures of the administration menu; a figure that is zero or not readable has no text, so its badge hides.
        public static LiveEntity Menu(AdminCounts counts) =>
            new("menu", null, null, Texts([.. new[] { AdminCounter.OpenDownloads, AdminCounter.FailedJobs, AdminCounter.OpenRequests }
                .Select(counter => (AdminCounts.Name(counter), counts.Of(counter) is int count and > 0 ? Count(count) : null))]));

        // Counts and the pace as the tiles first render them.
        public static string Count(int count) => count.ToString(CultureInfo.CurrentCulture);

        public static LiveEntity Wanted(UiText texts, LiveWantedV1 wanted)
        {
            var phase = wanted.IsWaiting ? "waiting" : wanted.IsPaused ? "paused" : LiveFacts.Marker(wanted.AcquisitionRequestStatusTypeId);
            var text = wanted.IsWaiting ? texts["activity.waiting"] : wanted.IsPaused ? texts["activity.state.Paused"]
                : LiveFacts.Phase(texts, wanted.AcquisitionRequestStatusTypeId, wanted.ProgressPercent, admin: true);
            return new LiveEntity(phase, null, wanted.ProgressPercent, Texts(("phase", text),
                ("facts", LiveFacts.Join(LiveFacts.Progress(null, wanted.SizeBytes, wanted.RemainingBytes), LiveFacts.Speed(wanted.BytesPerSecond), LiveFacts.Eta(wanted.EtaSeconds))),
                ("release", wanted.ReleaseTitle), ("source", LiveFacts.Join(wanted.IndexerName, wanted.ClientName))));
        }

        // The pace of a running download; a paused one says so in the same place and style, so the column never loses its value.
        public static string OperationSpeed(UiText texts, OperationStatusType status, DownloadStateType? state, long? bytesPerSecond) =>
            status is OperationStatusType.Queued or OperationStatusType.Running && state == DownloadStateType.Paused ? texts["activity.state.Paused"] : LiveFacts.Speed(bytesPerSecond);

        // The queue's size and time left: "42 % · 3.1 GB / 7.4 GB · 12 min".
        public static string OperationDetail(int? percent, long? sizeBytes, long? remainingBytes, int? etaSeconds) =>
            LiveFacts.Join(LiveFacts.Progress(percent, sizeBytes, remainingBytes), LiveFacts.Eta(etaSeconds));

        // How far a download is, as its own column: "74%".
        public static string Percent(int? percent) => percent is { } value ? value.ToString(CultureInfo.CurrentCulture) + "%" : "";

        // The queue's Status column as the page first renders it: waiting, else what the client does with a running download, else the job's status.
        public static string OperationStatus(UiText texts, bool isWaiting, OperationStatusType status, DownloadStateType? state = null) =>
            isWaiting ? texts["activity.waiting"] : state is { } known ? texts["activity.state." + known] : texts["operation.status." + status];

        // Downloads report acquisition outcomes; scans and backups report service errors.
        public static string Reason(UiText texts, OperationType type, string? key)
        {
            var resource = (type == OperationType.Download ? "acquisition.outcome." : "errors.") + key;
            return key is not null && texts.Has(resource) ? texts[resource] : "";
        }

        private static Dictionary<string, string> Texts(params (string Name, string? Value)[] values) =>
            values.Where(value => !string.IsNullOrEmpty(value.Value)).ToDictionary(value => value.Name, value => value.Value!, StringComparer.Ordinal);
    }
}
