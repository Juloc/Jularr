using System.Globalization;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Frontend.Framework;
using Jularr.Frontend.Modules.Live;

namespace Jularr.Frontend.Pages.Admin.Queue
{
    // Where an operation stands, as the Queue (downloads) and the Jobs entry's Tasks (Jularr's own work) show it: the state pill, a bar of how far it is and one line of why it waits or failed.
    // The live stream keeps the label, the bar and the reason current; the pill's tone follows the phase (Queue.entry.scss).
    internal static class OperationState
    {
        public static IUiComponent Status(ActivityItemV1 item, UiText texts, FrontendAssets assets)
        {
            var says = Says(item);
            var phase = Phase(item);
            var label = LivePart("status", LiveEntities.OperationStatus(texts, item.IsWaiting, item.OperationStatusTypeId, says ? null : item.DownloadStateTypeId), "state-chip__live");
            return new Div(new StateChip(label, UiTone.Neutral, new Icon(PhaseIcon(phase), assets, 14)),
                LiveBar(says ? null : item.ProgressPercent),
                LivePart("reason", says ? LiveEntities.Reason(texts, item.OperationTypeId, item.ReasonKey) : "", "live-progress__facts"))
                .Class("live-progress list-phase activity-status").Attribute("data-live", LiveKeys.Operation(item.Id)).Attribute("data-phase", phase);
        }

        // The stream sets this marker; the state pill takes its tone from it.
        public static string Phase(ActivityItemV1 item)
        {
            var running = item.OperationStatusTypeId is OperationStatusType.Queued or OperationStatusType.Running;
            return (item.IsWaiting ? "waiting" : (running && item.DownloadStateTypeId is { } state ? state.ToString() : item.OperationStatusTypeId.ToString())).ToLowerInvariant();
        }

        public static string PhaseIcon(string phase) => phase switch
        {
            "downloading" => "download",
            "paused" => "pause",
            "postprocessing" or "running" => "sync",
            "completed" => "check",
            "failed" => "alert",
            _ => "clock"
        };

        // A waiting or failed entry says why instead of how far it is.
        public static bool Says(ActivityItemV1 item) => item.ReasonKey is not null && (item.IsWaiting || item.OperationStatusTypeId is OperationStatusType.Failed or OperationStatusType.Queued or OperationStatusType.Running);

        public static IUiComponent LivePart(string name, string text, string className)
        {
            var part = new Span(new ValueText(text)).Class(className).Attribute("data-live-text", name);
            return text.Length == 0 ? part.Attribute("hidden", "hidden") : part;
        }

        // An entry that says why it waits or failed has no bar: the stream drops it too, so the row keeps its height when the first snapshot arrives.
        public static IUiComponent LiveBar(int? percent)
        {
            var bar = new Progress().Attribute("max", "100").TextAttribute("aria-label", "live.progress").Class("live-progress__bar").Attribute("data-live-value", "percent");
            return percent is { } value ? bar.Attribute("value", value.ToString(CultureInfo.InvariantCulture)) : bar.Attribute("hidden", "hidden");
        }
    }
}
