using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Enums.Acquisition;

namespace Jularr.Frontend.Framework
{
    // The names under which the live stream publishes what a page shows; a marked element declares one of them in data-live.
    public static class LiveKeys
    {
        // The profile's own request, and the newest one of the profile for a Work.
        public static string Request(long id) => "request:" + id.ToString(CultureInfo.InvariantCulture);
        public static string RequestOfWork(long workId) => "request-work:" + workId.ToString(CultureInfo.InvariantCulture);

        // What an administrator sees: any request, a download or job of the queue, and the downloads of one target.
        public static string AdminRequest(long id) => "admin-request:" + id.ToString(CultureInfo.InvariantCulture);
        public static string Operation(long id) => "operation:" + id.ToString(CultureInfo.InvariantCulture);
        public static string Wanted(long id) => "wanted:" + id.ToString(CultureInfo.InvariantCulture);

        // The numbers of the queue's tiles.
        public const string Queue = "queue";
    }

    // Phase names, percent, speed and time left as text, the same for a page's first rendering and for every stream event.
    public static class LiveFacts
    {
        // "Downloading 43 %": a person's request speaks plainly, an administrator reads the request's status.
        public static string Phase(UiText texts, AcquisitionRequestStatusType phase, int? percent, bool admin = false) =>
            texts[(admin ? "request.status." : "request.state.") + phase] + (phase == AcquisitionRequestStatusType.Downloading && percent is { } value ? " " + value.ToString(CultureInfo.CurrentCulture) + " %" : "");

        // The phase as a CSS state.
        public static string Marker(AcquisitionRequestStatusType phase) => phase.ToString().ToLowerInvariant();

        // How many of the five steps (requested, searching, downloading, importing, available) are reached; a request that ended otherwise has none.
        public static int Step(AcquisitionRequestStatusType phase) => phase switch
        {
            AcquisitionRequestStatusType.Open or AcquisitionRequestStatusType.Approved => 1,
            AcquisitionRequestStatusType.Searching => 2,
            AcquisitionRequestStatusType.Downloading => 3,
            AcquisitionRequestStatusType.Importing => 4,
            AcquisitionRequestStatusType.Completed => 5,
            _ => 0
        };

        // "42 % · 3.1 GB / 7.4 GB"; sizes only where the client reports them.
        public static string Progress(int? percent, long? sizeBytes, long? remainingBytes)
        {
            var text = percent is { } value ? value.ToString(CultureInfo.InvariantCulture) + " %" : "";
            if (sizeBytes is not { } size)
            {
                return text;
            }

            var done = remainingBytes is { } remaining ? Math.Max(0, size - remaining) : (long?)null;
            var amount = done is { } bytes ? ByteSize.Format(bytes) + " / " + ByteSize.Format(size) : ByteSize.Format(size);
            return text.Length == 0 ? amount : text + " · " + amount;
        }

        public static string Speed(long? bytesPerSecond) => bytesPerSecond is { } speed ? ByteSize.Format(speed) + "/s" : "";

        public static string Eta(int? seconds) => seconds switch
        {
            null => "",
            >= 3600 => (seconds.Value / 3600).ToString(CultureInfo.InvariantCulture) + " h " + (seconds.Value % 3600 / 60).ToString(CultureInfo.InvariantCulture) + " min",
            >= 60 => (seconds.Value / 60).ToString(CultureInfo.InvariantCulture) + " min",
            _ => "< 1 min"
        };

        // The time a person's request still needs, as a sentence part.
        public static string EtaLeft(UiText texts, int? seconds) => seconds is null ? "" : string.Format(CultureInfo.CurrentCulture, texts["live.eta"], Eta(seconds));

        public static string Join(params string?[] parts) => string.Join(" · ", parts.Where(part => !string.IsNullOrEmpty(part)));
    }

    // A request's progress for the person who made it, or for an administrator, or the downloads of one target: the phase, a bar while a percent is known,
    // the speed and time left, and for an administrator the release with its indexer and client. A script keeps it current; without one it shows what the page was made with.
    public sealed class LiveProgress(string key) : IUiComponent
    {
        private AcquisitionRequestStatusType? _status;
        private bool _admin;
        private bool _steps;
        private bool _details;
        private bool _hidden;
        private bool _compact;

        // A request's phase from the page's own data.
        public LiveProgress Request(AcquisitionRequestStatusType status, bool admin = false)
        {
            _status = status;
            _admin = admin;
            return this;
        }

        // The five steps of a request with the reached ones marked.
        public LiveProgress Steps()
        {
            _steps = true;
            return this;
        }

        // Release, indexer, client and the reason a download waits or failed; only for administrators.
        public LiveProgress Details()
        {
            _details = true;
            return this;
        }

        // For a list row: the phase above thin step segments whose names are tooltips, no bar of its own.
        public LiveProgress Compact()
        {
            _compact = true;
            return this;
        }

        // Shown only while the stream knows something for the key.
        public LiveProgress Optional()
        {
            _hidden = true;
            return this;
        }

        private HtmlElement Step(AcquisitionRequestStatusType step)
        {
            var item = new Li(new Text("request.state." + step)).Class("live-steps__item");
            return _compact ? item.TextAttribute("title", "request.state." + step) : item;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var parts = new List<IUiComponent>();
            if (_steps)
            {
                parts.Add(new Ol([.. new[] { AcquisitionRequestStatusType.Approved, AcquisitionRequestStatusType.Searching, AcquisitionRequestStatusType.Downloading,
                    AcquisitionRequestStatusType.Importing, AcquisitionRequestStatusType.Completed }.Select(step => (IUiComponent)Step(step))]).Class("live-steps"));
            }

            // Beside the steps the phase is read, not shown twice; where the steps have no room it is shown instead of their names.
            parts.Add(new Span(new ValueText(_status is { } status ? LiveFacts.Phase(texts, status, null, _admin) : "")).Class(_steps ? "live-progress__phase live-progress__phase--steps" : "live-progress__phase")
                .Attribute("data-live-text", "phase"));
            parts.Add(new Progress().Attribute("max", "100").Attribute("hidden", "hidden").Attribute("data-live-value", "percent").TextAttribute("aria-label", "live.progress").Class("live-progress__bar"));
            parts.Add(new Span().Class("live-progress__facts").Attribute("data-live-text", "facts").Attribute("hidden", "hidden"));
            if (_details)
            {
                parts.Add(new Span().Class("live-progress__detail").Attribute("data-live-text", "release").Attribute("hidden", "hidden"));
                parts.Add(new Span().Class("live-progress__detail").Attribute("data-live-text", "source").Attribute("hidden", "hidden"));
                parts.Add(new Span().Class("live-progress__detail live-progress__detail--error").Attribute("data-live-text", "error").Attribute("hidden", "hidden"));
            }

            var block = new Div([.. parts]).Class(_compact ? "live-progress live-progress--compact" : "live-progress").Attribute("data-live", key);
            if (_status is { } known) block.Attribute("data-phase", LiveFacts.Marker(known)).Attribute("data-step", LiveFacts.Step(known).ToString(CultureInfo.InvariantCulture));
            if (_hidden) block.Attribute("data-live-optional", "").Attribute("hidden", "hidden");
            block.WriteTo(writer, encoder, texts);
        }
    }

    // The button that names a request's phase, with its percent while it downloads, and leads to the requests.
    public sealed class LivePhaseButton(string key, AcquisitionRequestStatusType status, string href) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            new A(new ValueText(LiveFacts.Phase(texts, status, null))).Class("button").Attribute("href", href).Attribute("data-live", key).Attribute("data-live-text", "phase")
                .Attribute("data-phase", LiveFacts.Marker(status)).WriteTo(writer, encoder, texts);
    }
}
