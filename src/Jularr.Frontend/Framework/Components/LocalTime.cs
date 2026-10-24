using System.Globalization;

namespace Jularr.Frontend.Framework
{
    // The server cannot know the viewer's timezone. UTC instants remain untouched in APIs and storage.
    public sealed class LocalTime : HtmlElement
    {
        public LocalTime(DateTime instant, string display = "datetime")
            : base("time", new ValueText(Fallback(instant, display)))
        {
            if (display is not ("datetime" or "date" or "time"))
                throw new ArgumentOutOfRangeException(nameof(display));
            Attribute("datetime", Iso(instant));
            Attribute("data-local-time", display);
        }

        private static DateTime AsUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // Timestamps from the API are UTC even if the Kind was lost during mapping.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        public static string Iso(DateTime value) =>
            AsUtc(value).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        // A no-script fallback must be clearly labelled UTC rather than pretending to be local.
        public static string Fallback(DateTime value, string display = "datetime") =>
            AsUtc(value).ToString(display switch
            {
                "datetime" => "g",
                "date" => "d",
                "time" => "t",
                _ => throw new ArgumentOutOfRangeException(nameof(display))
            }, CultureInfo.CurrentCulture) + " UTC";

        // Allows localized text containing one {0} date placeholder to keep its translated order.
        public static IUiComponent Format(string template, DateTime instant, string display = "datetime")
        {
            const string marker = "\u001f";
            var rendered = string.Format(CultureInfo.CurrentCulture, template, marker);
            var index = rendered.IndexOf(marker, StringComparison.Ordinal);
            return index < 0 ? new ValueText(rendered) : new Span(
                new ValueText(rendered[..index]), new LocalTime(instant, display), new ValueText(rendered[(index + marker.Length)..]));
        }
    }
}
