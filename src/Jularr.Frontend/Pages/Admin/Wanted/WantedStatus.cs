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
            if (item.HasMedia) return new Text("wanted.available");
            if (item.IsDownloading)
            {
                return item.ProgressPercent is { } percent
                    ? new Span(new Text("wanted.downloading"), new ValueText(" " + percent.ToString(CultureInfo.InvariantCulture) + "%"))
                    : new Text("wanted.downloading");
            }

            if (item.IsPaused) return new Text("wanted.paused");

            return new Text(ReasonKey(item) ?? "wanted.requested");
        }

        // The list's pill says it in a short word; what the last search said stays whole in a line under it (ReasonKey).
        public static IUiComponent Short(WantedDetailsV1 item) => ReasonKey(item) is null ? Of(item) : new Text("wanted.requested");

        public static string? ReasonKey(WantedDetailsV1 item) => item.HasMedia || item.IsDownloading || item.IsPaused || item.OutcomeKey is not { } outcome ? null : "acquisition.outcome." + outcome;

        public static string Home(long wantedItemId) => "/Admin/Wanted/" + wantedItemId.ToString(CultureInfo.InvariantCulture);

        // The manual search of a target, or of its season, episode or volume: the page, which a link of data-search-dialog opens as a dialog instead.
        public static string Search(long wantedItemId, int? parentUnitNumber = null, int? unitNumber = null) => Home(wantedItemId) + "/Search"
            + (parentUnitNumber is null && unitNumber is null ? "" : "?" + string.Join("&", new[] { parentUnitNumber is { } parent ? "parentUnitNumber=" + parent.ToString(CultureInfo.InvariantCulture) : null,
                unitNumber is { } unit ? "unitNumber=" + unit.ToString(CultureInfo.InvariantCulture) : null }.OfType<string>()));
    }
}
