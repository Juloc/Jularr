using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Mapping;
using Jularr.Web.Features.MediaMapping;
using Jularr.Web.Features.Tracking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Settings;

/// <summary>
/// Owner-gated Metadata &amp; Mapping surface (issue #525, epic #510). Beyond the dismiss-only review
/// queue it now hosts: configurable provider roles (global defaults and per-anime overrides), and the
/// range-mapping apply workflow for a selected anime — candidate confidence, exact/partial/missing/
/// conflict/unmapped states, a local-vs-provider side-by-side view, per-episode overrides, specials
/// handling, an explicit unmapped state, preview-before-apply and a durable audit history. The stores
/// are raw-ADO.NET derived state (no EF entities); progress is never touched because it keys on the
/// stable EpisodeId while mappings key on AnimeId + local range.
/// </summary>
[Authorize(Policy = JularrPolicies.MappingEdit)]
public sealed class MappingReviewModel(
    MediaMappingReviewStore reviewStore,
    CurrentAccountContext account,
    AppDbContext db,
    AniListAccountStore mappingStore) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public IReadOnlyList<MediaMappingReviewTask> Tasks { get; private set; } = [];
    public IReadOnlyList<ProviderRoleAssignment> GlobalRoles { get; private set; } = [];

    // Per-anime context (only populated when AnimeId is set).
    public Guid? AnimeId { get; private set; }
    public string AnimeTitle { get; private set; } = "";
    public IReadOnlyList<ProviderRoleAssignment> WorkRoles { get; private set; } = [];
    public IReadOnlyList<AnimeMappingRange> AppliedRanges { get; private set; } = [];
    public AnimeMappingPreview? LivePreview { get; private set; }
    public AnimeMappingPreview? ProposedPreview { get; private set; }

    /// <summary>The preview to render: the proposed one when previewing, otherwise the live applied one.</summary>
    public AnimeMappingPreview? ShownPreview => ProposedPreview ?? LivePreview;
    public IReadOnlyList<MediaMappingReviewCandidate> Candidates { get; private set; } = [];
    public IReadOnlyList<MappingAuditEntry> Audit { get; private set; } = [];
    public string? RangeError { get; private set; }

    [BindProperty]
    public int RangeSeason { get; set; } = 1;

    [BindProperty]
    public int RangeLocalStart { get; set; } = 1;

    [BindProperty]
    public int? RangeLocalEnd { get; set; }

    [BindProperty]
    public string RangeExternalId { get; set; } = "";

    [BindProperty]
    public int RangeRemoteStart { get; set; } = 1;

    [BindProperty]
    public int? RangeRemoteCount { get; set; }

    [BindProperty]
    public bool RangeUnmapped { get; set; }

    private ProviderRoleAssignmentStore Roles() => new(db);
    private MappingAuditStore AuditStore() => new(db);
    private AnimeMappingApplyService Apply() => new(db, mappingStore, AuditStore());

    public async Task<IActionResult> OnGetAsync(Guid? animeId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        await LoadCommonAsync(cancellationToken);
        if (animeId is { } id)
        {
            await LoadAnimeAsync(id, previewProposed: false, cancellationToken);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDismissAsync(Guid id, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        var removed = await reviewStore.DismissAsync(id, cancellationToken);
        TempData["Status"] = removed
            ? Ui["settings.mappingReview.dismissed"]
            : Ui["settings.mappingReview.alreadyGone"];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetGlobalRoleAsync(string role, string provider, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        if (MappingProviderRoles.TryParse(role, out var parsedRole))
        {
            try
            {
                await Roles().SetGlobalDefaultAsync(parsedRole, provider, cancellationToken);
                TempData["Status"] = Ui["settings.mapping.roles.saved"];
            }
            catch (InvalidOperationException)
            {
                TempData["Status"] = Ui["settings.mapping.roles.invalid"];
            }
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetWorkRoleAsync(Guid animeId, string role, string? provider, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        if (MappingProviderRoles.TryParse(role, out var parsedRole))
        {
            try
            {
                await Roles().SetWorkOverrideAsync(animeId, parsedRole, provider, cancellationToken);
                TempData["Status"] = Ui["settings.mapping.roles.saved"];
            }
            catch (InvalidOperationException)
            {
                TempData["Status"] = Ui["settings.mapping.roles.invalid"];
            }
        }

        return RedirectToPage(new { animeId });
    }

    public async Task<IActionResult> OnPostPreviewRangeAsync(Guid animeId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        await LoadCommonAsync(cancellationToken);
        await LoadAnimeAsync(animeId, previewProposed: true, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostApplyRangeAsync(Guid animeId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        await LoadCommonAsync(cancellationToken);
        var apply = Apply();
        var localEpisodes = await apply.LoadLocalEpisodesAsync(animeId, cancellationToken);
        var desired = await BuildDesiredAsync(apply, animeId, localEpisodes, cancellationToken);
        if (desired is null)
        {
            await LoadAnimeAsync(animeId, previewProposed: true, cancellationToken);
            return Page();
        }

        var result = await apply.ApplyAsync(animeId, desired, account.ProfileId, cancellationToken);
        if (result.Applied)
        {
            await DismissAnimeTasksAsync(animeId, cancellationToken);
            TempData["Status"] = Ui["settings.mapping.apply.applied"];
            return RedirectToPage(new { animeId });
        }

        TempData["Status"] = Ui["settings.mapping.apply.blocked"];
        await LoadAnimeAsync(animeId, previewProposed: true, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveRangeAsync(Guid animeId, int season, int localStart, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        var apply = Apply();
        var remaining = (await apply.LoadAppliedRangesAsync(animeId, cancellationToken))
            .Where(x => !(x.SeasonNumber == season && x.LocalStart == localStart))
            .ToArray();

        var result = await apply.ApplyAsync(animeId, remaining, account.ProfileId, cancellationToken);
        TempData["Status"] = result.Applied
            ? Ui["settings.mapping.apply.removed"]
            : Ui["settings.mapping.apply.blocked"];
        return RedirectToPage(new { animeId });
    }

    public async Task<IActionResult> OnPostMarkUnmappedAsync(Guid animeId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        await Apply().MarkUnmappedAsync(animeId, account.ProfileId, cancellationToken);
        await DismissAnimeTasksAsync(animeId, cancellationToken);
        TempData["Status"] = Ui["settings.mapping.apply.markedUnmapped"];
        return RedirectToPage(new { animeId });
    }

    private async Task LoadCommonAsync(CancellationToken cancellationToken)
    {
        Tasks = await reviewStore.ListAsync(cancellationToken);
        GlobalRoles = await Roles().ResolveGlobalDefaultsAsync(cancellationToken);
    }

    private async Task LoadAnimeAsync(Guid animeId, bool previewProposed, CancellationToken cancellationToken)
    {
        var title = await db.Anime
            .AsNoTracking()
            .Where(x => x.Id == animeId)
            .Select(x => x.Title)
            .SingleOrDefaultAsync(cancellationToken);
        if (title is null)
        {
            return;
        }

        AnimeId = animeId;
        AnimeTitle = title;
        WorkRoles = await Roles().ResolveForWorkAsync(animeId, cancellationToken);
        Audit = await AuditStore().ListForAnimeAsync(animeId, cancellationToken);

        var apply = Apply();
        var localEpisodes = await apply.LoadLocalEpisodesAsync(animeId, cancellationToken);
        AppliedRanges = await apply.LoadAppliedRangesAsync(animeId, cancellationToken);
        LivePreview = AnimeMappingPlanner.BuildPreview(localEpisodes, AppliedRanges);

        var pending = await reviewStore.FindPendingAsync("anime", animeId.ToString(), cancellationToken: cancellationToken);
        Candidates = pending?.Candidates ?? [];

        if (previewProposed)
        {
            var desired = await BuildDesiredAsync(apply, animeId, localEpisodes, cancellationToken);
            if (desired is not null)
            {
                ProposedPreview = AnimeMappingPlanner.BuildPreview(localEpisodes, desired);
            }
        }
    }

    /// <summary>Applied ranges plus the one range currently in the add-range form, or null on invalid input.</summary>
    private async Task<IReadOnlyList<AnimeMappingRange>?> BuildDesiredAsync(
        AnimeMappingApplyService apply,
        Guid animeId,
        IReadOnlyList<LocalEpisodeRef> localEpisodes,
        CancellationToken cancellationToken)
    {
        if (RangeSeason < 0 || RangeLocalStart <= 0)
        {
            RangeError = Ui["settings.mapping.apply.invalidRange"];
            return null;
        }

        var seasonMax = localEpisodes
            .Where(x => x.SeasonNumber == RangeSeason)
            .Select(x => x.Number)
            .DefaultIfEmpty(RangeLocalStart)
            .Max();
        var localEnd = RangeLocalEnd ?? seasonMax;
        if (localEnd < RangeLocalStart)
        {
            RangeError = Ui["settings.mapping.apply.invalidRange"];
            return null;
        }

        var externalId = (RangeExternalId ?? "").Trim();
        if (!RangeUnmapped && (externalId.Length == 0 || RangeRemoteStart <= 0))
        {
            RangeError = Ui["settings.mapping.apply.invalidRange"];
            return null;
        }

        var newRange = new AnimeMappingRange(
            RangeSeason,
            RangeLocalStart,
            localEnd,
            MappingProviders.AniList,
            RangeUnmapped ? "" : externalId,
            RangeUnmapped ? 0 : RangeRemoteStart,
            PreferredTitle: null,
            RemoteEpisodeCount: RangeRemoteCount,
            Unmapped: RangeUnmapped);

        // Replace any existing range with the same season+start; otherwise append.
        var applied = await apply.LoadAppliedRangesAsync(animeId, cancellationToken);
        var desired = applied
            .Where(x => !(x.SeasonNumber == newRange.SeasonNumber && x.LocalStart == newRange.LocalStart))
            .Append(newRange)
            .OrderBy(x => x.SeasonNumber)
            .ThenBy(x => x.LocalStart)
            .ToArray();
        return desired;
    }

    private async Task DismissAnimeTasksAsync(Guid animeId, CancellationToken cancellationToken)
    {
        var localId = animeId.ToString();
        var stale = (await reviewStore.ListAsync(cancellationToken))
            .Where(x => string.Equals(x.MediaType, "anime", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.LocalId, localId, StringComparison.Ordinal))
            .Select(x => x.Id)
            .ToArray();
        foreach (var id in stale)
        {
            await reviewStore.DismissAsync(id, cancellationToken);
        }
    }

    public static string? TargetUrl(MediaMappingReviewTask task)
    {
        if (!Guid.TryParse(task.LocalId, out var id))
        {
            return null;
        }

        return task.MediaType.ToLowerInvariant() switch
        {
            "anime" => $"/Settings/MappingReview?animeId={id}",
            "manga" => $"/Manga/Series/{id}",
            "novel" => $"/Novels/Work/{id}",
            _ => null
        };
    }
}
