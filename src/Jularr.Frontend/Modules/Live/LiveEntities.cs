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
            return new LiveEntity(phase, null, operation.ProgressPercent, Texts(("status", OperationStatus(texts, operation.IsWaiting, operation.OperationStatusTypeId)),
                ("reason", says ? Reason(texts, operation.OperationTypeId, operation.ReasonKey) : null),
                ("state", says ? null : OperationState(texts, operation.DownloadStateTypeId)),
                ("facts", says ? null : LiveFacts.Join(LiveFacts.Progress(operation.ProgressPercent, operation.SizeBytes, operation.RemainingBytes), LiveFacts.Speed(operation.BytesPerSecond), LiveFacts.Eta(operation.EtaSeconds)))));
        }

        public static LiveEntity Wanted(UiText texts, LiveWantedV1 wanted)
        {
            var phase = wanted.IsWaiting ? "waiting" : wanted.IsPaused ? "paused" : LiveFacts.Marker(wanted.AcquisitionRequestStatusTypeId);
            var text = wanted.IsWaiting ? texts["activity.waiting"] : wanted.IsPaused ? texts["activity.state.Paused"]
                : LiveFacts.Phase(texts, wanted.AcquisitionRequestStatusTypeId, wanted.ProgressPercent, admin: true);
            return new LiveEntity(phase, null, wanted.ProgressPercent, Texts(("phase", text),
                ("facts", LiveFacts.Join(LiveFacts.Progress(null, wanted.SizeBytes, wanted.RemainingBytes), LiveFacts.Speed(wanted.BytesPerSecond), LiveFacts.Eta(wanted.EtaSeconds))),
                ("release", wanted.ReleaseTitle), ("source", LiveFacts.Join(wanted.IndexerName, wanted.ClientName))));
        }

        // The queue's Status column and the client's own state, as the page first renders them.
        public static string OperationStatus(UiText texts, bool isWaiting, OperationStatusType status) => texts[isWaiting ? "activity.waiting" : "operation.status." + status];

        public static string OperationState(UiText texts, DownloadStateType? state) => state is { } known ? texts["activity.state." + known] : "";

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
