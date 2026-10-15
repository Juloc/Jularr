using System.Globalization;
using Jularr.Backend.Data.Admin.Wanted.V1;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin.Wanted
{
    // The state of a target as the list and its own page show it.
    internal static class WantedStatus
    {
        public static IUiComponent Of(WantedDetailsV1 item)
        {
            // Paused and available targets need no further acquisition.
            if (item.IsPaused) return new Text("wanted.paused");
            if (item.HasMedia) return new Text("wanted.available");
            if (item.IsDownloading)
            {
                return item.ProgressPercent is { } percent
                    ? new Span(new Text("wanted.downloading"), new ValueText(" " + percent.ToString(CultureInfo.InvariantCulture) + "%"))
                    : new Text("wanted.downloading");
            }

            return new Text(item.OutcomeKey is { } outcome ? "acquisition.outcome." + outcome : "wanted.requested");
        }

        public static string Home(long wantedItemId) => "/Admin/Wanted/" + wantedItemId.ToString(CultureInfo.InvariantCulture);
    }
}
