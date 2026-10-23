using System.Globalization;

namespace Jularr.Frontend.Framework
{
    // How full a volume is: the bar with the used percent, and a legend of the free space (left) and the used share (right). Without a measurement the bar is empty and the legend says so.
    public sealed class SpaceBar : HtmlElement
    {
        public SpaceBar(long? freeBytes, long? totalBytes, string ariaLabel, string freeText, string usedText, string unknownText) : base("div", Parts(freeBytes, totalBytes, ariaLabel, freeText, usedText, unknownText))
        {
            Class("space-bar");
        }

        // The used share in whole percent, or nothing when the volume has not been measured.
        public static int? Percent(long? freeBytes, long? totalBytes) =>
            freeBytes is { } free && totalBytes is > 0 and var total ? (int)Math.Clamp(Math.Round((total - free) * 100d / total), 0, 100) : null;

        private static IUiComponent[] Parts(long? freeBytes, long? totalBytes, string ariaLabel, string freeText, string usedText, string unknownText)
        {
            var percent = Percent(freeBytes, totalBytes);
            var bar = new Progress().Attribute("max", "100").Attribute("value", (percent ?? 0).ToString(CultureInfo.InvariantCulture)).Attribute("aria-label", ariaLabel).Class("space-bar__bar");
            var legend = percent is null
                ? new Div(new Span(new ValueText(unknownText))).Class("space-bar__legend")
                : new Div(new Span(new ValueText(freeText)), new Strong(new ValueText(usedText))).Class("space-bar__legend");
            return [bar, legend];
        }
    }
}
