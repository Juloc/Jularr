using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Admin;

/// <summary>
/// Admin → Media detail: one anime with its seasons, episodes and real local files, its monitoring,
/// quality profile, provider mapping, imports and recent activity. Everything is read from stored rows
/// (no file is probed while rendering). Acting reuses what exists: acquisition settings and searches go
/// to the acquisition page, folder re-scan, re-analysis and optimization to the anime repair page; only
/// season/episode monitoring and the re-analysis of a single file are handled here.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class MediaDetailModel(
    AppDbContext db,
    AdminMediaDetailService details,
    AnimeMonitoringStore monitoring,
    CurrentAccountContext currentAccount,
    ILogger<MediaDetailModel> logger,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    public const string ReanalyzeOperationKind = "anime-repair-reanalyze-media";

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public AdminMediaDetail? Detail { get; private set; }

    /// <summary>Whether the details could not be read.</summary>
    public bool Failed { get; private set; }

    /// <summary>Whether the AniList grouping is selected (display only).</summary>
    public bool AniListView { get; private set; }

    /// <summary>The group and episode the address asks to keep open.</summary>
    public string? OpenGroup { get; private set; }

    public string? OpenEpisode { get; private set; }

    public IReadOnlyList<AdminMediaGroup> Groups { get; private set; } = [];

    /// <summary>The result of the action that sent the admin back here.</summary>
    public string? Notice => TempData["AcquisitionNotice"] as string;

    public string? Error => TempData["AcquisitionError"] as string ?? TempData["Error"] as string;

    public DateTime NowUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Links to pages that need a stronger permission than this one appear only for those who hold it.</summary>
    public bool CanRename => currentAccount.Can(JularrPolicies.MediaRename);

    public bool IsOwner => currentAccount.IsOwner;

    /// <summary>The address of this page for a grouping, with the group and episode that stay open.</summary>
    public static string Href(Guid id, bool aniList, string? open = null, string? episode = null)
    {
        var parts = new List<string>();
        if (aniList)
        {
            parts.Add($"view={AdminMediaDetailView.AniListView}");
        }

        if (!string.IsNullOrEmpty(open))
        {
            parts.Add($"open={Uri.EscapeDataString(open)}");
        }

        if (!string.IsNullOrEmpty(episode))
        {
            parts.Add($"ep={Uri.EscapeDataString(episode)}");
        }

        var path = $"/Admin/Media/{id:D}";
        return parts.Count == 0 ? path : $"{path}?{string.Join('&', parts)}";
    }

    public static string Stamp(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    public static string Stamp(DateTimeOffset value) => Stamp(value.UtcDateTime);

    /// <summary>Whether a group starts open: the one the address names, or the only one.</summary>
    public bool IsOpen(AdminMediaGroup group) =>
        OpenGroup is null ? Groups.Count == 1 : string.Equals(OpenGroup, group.Key, StringComparison.Ordinal);

    public string GroupName(AdminMediaGroup group) =>
        group.Title
        ?? (group.Season is null ? Ui["admin.media.group.unmatched"]
            : group.Season == 0 ? Ui["library.anime.specials"]
            : Ui.Format("library.watch.season", ("number", group.Season.Value)));

    public string EpisodeCount(int count) =>
        count == 1 ? Ui["library.anime.oneEpisode"] : Ui.Format("library.index.episodeCount", ("count", count));

    /// <summary>The release status of the provider match, in words.</summary>
    public string? ProviderStatus() => Jularr.Web.Ui.MediaBannerCardModel.MapStatus(Detail?.Metadata?.Status) switch
    {
        Jularr.Web.Ui.MediaReleaseStatus.Ongoing => Ui["library.mediaCard.status.ongoing"],
        Jularr.Web.Ui.MediaReleaseStatus.Finished => Ui["library.mediaCard.status.finished"],
        Jularr.Web.Ui.MediaReleaseStatus.Upcoming => Ui["library.mediaCard.status.upcoming"],
        Jularr.Web.Ui.MediaReleaseStatus.Hiatus => Ui["library.mediaCard.status.hiatus"],
        Jularr.Web.Ui.MediaReleaseStatus.Cancelled => Ui["library.mediaCard.status.cancelled"],
        _ => null
    };

    public async Task<IActionResult> OnGetAsync(Guid id, string? view, string? open, string? ep, CancellationToken cancellationToken)
    {
        if (!await IsAnimeEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        NowUtc = DateTime.UtcNow;
        AniListView = AdminMediaDetailView.IsAniList(view);
        OpenGroup = string.IsNullOrWhiteSpace(open) ? null : open.Trim();
        OpenEpisode = string.IsNullOrWhiteSpace(ep) ? null : ep.Trim();

        try
        {
            Detail = await details.LoadAsync(id, cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or FormatException or IOException or JsonException or InvalidDataException)
        {
            logger.LogError(exception, "The media details of {AnimeId} could not be read.", id);
            Failed = true;
            return Page();
        }

        if (Detail is null)
        {
            return NotFound();
        }

        Groups = AniListView
            ? AdminMediaDetailView.GroupByAniList(Detail.Episodes, Detail.Mapping.Ranges, Detail.Mapping.Title)
            : AdminMediaDetailView.GroupBySeason(Detail.Episodes);
        return Page();
    }

    /// <summary>Monitors or unmonitors a whole season; the episodes inside it follow.</summary>
    public async Task<IActionResult> OnPostSeasonMonitorAsync(
        Guid id,
        int season,
        bool monitored,
        string? view,
        CancellationToken cancellationToken)
    {
        if (!await IsAnimeEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        if (await AnimeKeyAsync(id, cancellationToken) is not { } animeKey)
        {
            return NotFound();
        }

        await monitoring.UpdateAsync(
            state => Apply(state, animeKey, settings => AdminMediaMonitoringEdit.SetSeason(settings, season, monitored)),
            cancellationToken);
        return Redirect(Href(id, AdminMediaDetailView.IsAniList(view), $"s{season}"));
    }

    /// <summary>Monitors or unmonitors one episode; the override is dropped when it matches what the season inherits.</summary>
    public async Task<IActionResult> OnPostEpisodeMonitorAsync(
        Guid id,
        int season,
        int episode,
        bool monitored,
        string? view,
        string? open,
        CancellationToken cancellationToken)
    {
        if (!await IsAnimeEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        if (await AnimeKeyAsync(id, cancellationToken) is not { } animeKey)
        {
            return NotFound();
        }

        await monitoring.UpdateAsync(
            state => Apply(state, animeKey, settings => AdminMediaMonitoringEdit.SetEpisode(settings, season, episode, monitored)),
            cancellationToken);
        return Redirect(Href(
            id,
            AdminMediaDetailView.IsAniList(view),
            string.IsNullOrWhiteSpace(open) ? $"s{season}" : open.Trim(),
            AdminMediaDetailView.EpisodeKey(season, episode)));
    }

    /// <summary>Forces one file through the same analysis path the repair page uses for all of them.</summary>
    public async Task<IActionResult> OnPostReanalyzeFileAsync(
        Guid id,
        Guid fileId,
        string? view,
        string? open,
        string? ep,
        [FromServices] MediaFileReanalysisService reanalysis,
        CancellationToken cancellationToken)
    {
        if (!await IsAnimeEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            var descriptor = new OperationDescriptor(ReanalyzeOperationKind, "Anime", "Re-analyse anime media", ProfileId: currentAccount.ProfileId);
            var result = await reanalysis.ReanalyzeAnimeFileAsync(id, fileId, descriptor, cancellationToken);
            if (!result.Found)
            {
                TempData["Error"] = Ui["admin.media.fileGone"];
            }
            else
            {
                TempData["AcquisitionNotice"] = result.Status == MediaAnalysisStatus.Succeeded ? Ui["admin.media.reanalyzed"] : Ui["admin.media.reanalyzeIncomplete"];
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or DbException)
        {
            logger.LogError(exception, "Re-analysing media file {FileId} of {AnimeId} failed.", fileId, id);
            TempData["Error"] = Ui["admin.media.reanalyzeFailed"];
        }

        return Redirect(Href(id, AdminMediaDetailView.IsAniList(view), open, ep));
    }

    private async Task<bool> IsAnimeEnabledAsync(CancellationToken cancellationToken) =>
        instanceModules is null
        || await instanceModules.IsEnabledAsync(
            InstanceModule.Anime,
            cancellationToken);

    private Task<string?> AnimeKeyAsync(Guid id, CancellationToken cancellationToken) =>
        db.Anime.AsNoTracking().Where(item => item.Id == id).Select(item => item.Key).SingleOrDefaultAsync(cancellationToken);

    private static MonitoringState Apply(MonitoringState state, string animeKey, Func<MonitorSettings, MonitorSettings> edit)
    {
        var anime = new Dictionary<string, MonitorSettings>(state.Anime, StringComparer.OrdinalIgnoreCase);
        var current = anime.TryGetValue(animeKey, out var found) ? found : AdminMediaMonitoringEdit.Empty(animeKey);
        anime[animeKey] = edit(current);
        return state with { Anime = anime };
    }
}
