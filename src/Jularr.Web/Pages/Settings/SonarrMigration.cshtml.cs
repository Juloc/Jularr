using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Ownership;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Sonarr;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Settings;

public sealed record SonarrMigrationRow(
    string AnimeKey,
    string Title,
    AnimeManagementMode Mode,
    bool HasExplicitDecision,
    SonarrObservedSeries? Series,
    int? SeriesId,
    string? SeriesTitle,
    bool SeriesSuggested,
    bool SonarrUnmonitoredByJularr,
    int ActiveSonarrDownloads,
    IReadOnlyList<OwnershipConflict> Conflicts);

[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class SonarrMigrationModel(
    AppDbContext db,
    SonarrObservationService observationService,
    SonarrMigrationService migrationService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Refresh { get; set; }

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public SonarrObservedState Sonarr { get; private set; } = SonarrObservedState.NotConfigured;
    public IReadOnlyList<SonarrMigrationRow> Rows { get; private set; } = [];
    public IReadOnlyList<OwnershipConflict> Conflicts { get; private set; } = [];
    public IReadOnlyList<AnimeMigrationEvent> RecentEvents { get; private set; } = [];
    public string? Notice => TempData["SonarrMigrationNotice"] as string;
    public string? Error => TempData["SonarrMigrationError"] as string;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        var snapshot = await observationService.GetSnapshotAsync(Refresh, cancellationToken);
        Sonarr = snapshot.Sonarr;
        Conflicts = SonarrParallelSafety.DetectConflicts(snapshot.State, snapshot.Sonarr);
        RecentEvents = snapshot.State.MigrationLog
            .AsEnumerable()
            .Reverse()
            .Take(20)
            .ToArray();

        var localAnime = await db.Anime
            .AsNoTracking()
            .OrderBy(anime => anime.Title)
            .Select(anime => new SonarrLocalAnime(anime.Id, anime.Title, anime.Key))
            .ToListAsync(cancellationToken);

        var suggestions = await SuggestSeriesAsync(localAnime, snapshot.Sonarr, cancellationToken);
        var query = Q?.Trim();
        var rows = new List<SonarrMigrationRow>();

        foreach (var anime in localAnime)
        {
            if (!string.IsNullOrEmpty(query) &&
                !anime.Title.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !anime.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            snapshot.State.Anime.TryGetValue(anime.Key, out var assignment);
            var suggested = assignment?.SonarrSeriesId is null &&
                            suggestions.TryGetValue(anime.Id, out var match)
                ? match
                : null;
            var seriesId = assignment?.SonarrSeriesId ?? suggested?.Id;
            var series = SonarrOwnershipRecognizer.FindSeries(snapshot.Sonarr, seriesId);

            rows.Add(new SonarrMigrationRow(
                anime.Key,
                anime.Title,
                assignment?.Mode ?? AnimeManagementMode.ReadOnlyCoexistence,
                assignment is not null,
                series,
                seriesId,
                series?.Title ?? assignment?.SonarrSeriesTitle ?? suggested?.Title,
                suggested is not null,
                assignment?.SonarrUnmonitoredByJularr ?? false,
                seriesId is null ? 0 : snapshot.Sonarr.Queue.Count(item => item.SeriesId == seriesId),
                Conflicts
                    .Where(conflict => conflict.AnimeKey.Equals(anime.Key, StringComparison.OrdinalIgnoreCase))
                    .ToArray()));
        }

        Rows = rows;
    }

    public async Task<IActionResult> OnPostApplyAsync(
        string animeKey,
        AnimeMigrationAction migrationAction,
        int? sonarrSeriesId,
        bool applySonarrMonitoring,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (string.IsNullOrWhiteSpace(animeKey) ||
            !Enum.IsDefined(migrationAction) ||
            !await db.Anime.AsNoTracking().AnyAsync(anime => anime.Key == animeKey, cancellationToken))
        {
            TempData["SonarrMigrationError"] = Ui["settings.sonarrMigration.unknownAction"];
            return RedirectToPage(new { Q });
        }

        var result = await migrationService.ApplyAsync(
            new AnimeMigrationRequest(
                animeKey,
                migrationAction,
                sonarrSeriesId is > 0 ? sonarrSeriesId : null,
                ApplySonarrMonitoring: applySonarrMonitoring),
            cancellationToken);

        TempData[result.Success ? "SonarrMigrationNotice" : "SonarrMigrationError"] =
            $"{animeKey}: {result.Message}";
        return RedirectToPage(new { Q });
    }

    public string DescribeMode(AnimeManagementMode mode) => mode switch
    {
        AnimeManagementMode.ReadOnlyCoexistence => Ui["settings.sonarrMigration.mode.readOnlyCoexistence"],
        AnimeManagementMode.ParallelAcquisition => Ui["settings.sonarrMigration.mode.parallelAcquisition"],
        AnimeManagementMode.JularrManaged => Ui["settings.sonarrMigration.mode.jularrManaged"],
        _ => mode.ToString()
    };

    private static async Task<IReadOnlyDictionary<Guid, SonarrSeriesItem>> SuggestSeriesAsync(
        IReadOnlyList<SonarrLocalAnime> localAnime,
        SonarrObservedState sonarr,
        CancellationToken cancellationToken)
    {
        if (sonarr.Status != SonarrObservationStatus.Observed || sonarr.Series.Count == 0)
        {
            return new Dictionary<Guid, SonarrSeriesItem>();
        }

        // Reuse the canonical Sonarr matcher and the artwork import's confirmed mappings.
        var previous = await new SonarrArtworkManifestStore().LoadAsync(cancellationToken);
        var series = sonarr.Series
            .Select(item => new SonarrSeriesItem(item.Id, item.Title, item.Path, []))
            .ToArray();
        return SonarrSeriesMatcher.Match(localAnime, series, previous);
    }
}
