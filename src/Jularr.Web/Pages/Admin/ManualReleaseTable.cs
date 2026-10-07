using Jularr.Web.Features.Acquisition.ManualSearch;
using Jularr.Web.Features.Acquisition.Search;
using Jularr.Web.Features.Localization;

namespace Jularr.Web.Pages.Admin;

/// <summary>One release of a Manual Search as the shared release table shows it, whatever media type found it.</summary>
/// <param name="Facts">What the release is, in short: format, quality, language, volumes.</param>
/// <param name="Reasons">Why the release is or is not taken, one sentence each, the identity finding first.</param>
public sealed record ManualReleaseRow(int? Score, string Title, string Meta, IReadOnlyList<string> Facts, IReadOnlyList<string> Reasons, ManualSearchVerdict Verdict, bool IsTried, string Identity, bool CanGrab);

/// <param name="GrabRoute">The route values of the page's Grab handler, for example the request or the album.</param>
public sealed record ManualReleaseTableView(IReadOnlyList<ManualReleaseRow> Rows, IDictionary<string, string> GrabRoute, UiTextBundle Ui);

/// <summary>What the Manga, Light Novel and Music Manual Search pages share when they turn a candidate into a row of the release table.</summary>
public static class ManualReleaseRows
{
    public static string Size(long? bytes) =>
        bytes switch
        {
            null => "—",
            >= 1024L * 1024 * 1024 => $"{bytes.Value / (1024d * 1024 * 1024):0.0} GB",
            >= 1024L * 1024 => $"{bytes.Value / (1024d * 1024):0.0} MB",
            _ => $"{bytes.Value / 1024d:0} KB"
        };

    public static string VerdictTone(ManualSearchVerdict verdict) =>
        verdict switch
        {
            ManualSearchVerdict.Eligible => "success",
            ManualSearchVerdict.Warning => "warning",
            _ => "danger"
        };

    /// <summary>The release's source line: where it came from, how it was found, how big and how old it is.</summary>
    public static string Meta(UiTextBundle ui, IEnumerable<string> sources, IReadOnlyList<QueryProvenance> provenance, long? sizeBytes, int? ageDays)
    {
        var parts = new List<string> { string.Join(", ", sources) };
        if (provenance.Count > 0)
        {
            parts.Add(provenance[0].Description);
        }

        parts.Add(Size(sizeBytes));
        parts.Add(ageDays is { } days ? ui.Format("admin.manualSearch.ageDays", ("days", days)) : "");
        return string.Join(" · ", parts.Where(part => part.Length > 0 && part != "—"));
    }

    public static string VerdictName(ManualSearchVerdict verdict) => verdict.ToString().ToLowerInvariant();

    public static string VerdictText(UiTextBundle ui, ManualReleaseRow row) => ui["admin.manualSearch.state." + (row.IsTried ? "tried" : VerdictName(row.Verdict))];
}
