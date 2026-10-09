using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Wanted;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Monitoring;
using Jularr.Web.Features.ReadingAcquisition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Admin.Manga;

/// <summary>
/// One Manga Work: its volumes and chapters with what the library holds, each with its own Monitoring switch, the request that fetches the missing ones and
/// the files in the library. Every change is a Monitoring command, a request through the shared access policy or a profile assignment; nothing here is
/// Manga-specific state.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminMedia)]
public sealed class WorkModel(
    AppDbContext db,
    MangaWorkAdminQuery query,
    MonitoringCommands monitoring,
    WantedReconciler wanted,
    AcquisitionRequestService requests,
    AcquisitionAccessStore requestStore,
    QualityProfileStore profiles,
    ReadingStructureService structure,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    public const int ChaptersPerPage = 40;

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public MangaWorkAdminView View { get; private set; } = null!;

    public int ChapterPage { get; private set; } = 1;

    public string? Notice => TempData["MangaWorkNotice"] as string;

    public string? Error => TempData["MangaWorkError"] as string;

    public async Task<IActionResult> OnGetAsync(long workId, int pageNumber, CancellationToken cancellationToken)
    {
        if (!await ModuleEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (await query.GetAsync(workId, cancellationToken) is not { } view)
        {
            return NotFound();
        }

        View = view;
        ChapterPage = Math.Max(1, pageNumber);
        return Page();
    }

    /// <summary>Switches the Work, a volume or a chapter on or off, or back to following its parent with <c>inherit</c>; the Wanted queue follows at once.</summary>
    public async Task<IActionResult> OnPostMonitorAsync(long workId, string scope, Guid? targetId, string state, CancellationToken cancellationToken)
    {
        if (!await ModuleEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        bool? monitored = state switch { "true" => true, "false" => false, "inherit" => null, _ => throw new BadHttpRequestException("Unknown monitoring state.") };
        bool changed;
        if (scope == "work")
        {
            changed = await monitoring.SetWorkAsync(workId, monitored ?? false, cancellationToken);
        }
        else
        {
            var belongs = targetId is { } id && scope switch
            {
                "volume" => await db.WorkVolumes.AnyAsync(volume => volume.Id == id && volume.WorkId == workId, cancellationToken),
                "chapter" => await db.WorkChapters.AnyAsync(chapter => chapter.Id == id && chapter.WorkId == workId, cancellationToken),
                _ => false
            };
            changed = belongs && await monitoring.SetAsync(scope == "volume" ? MonitoringTargetKind.Volume : MonitoringTargetKind.Chapter, targetId!.Value, monitored, cancellationToken) == workId;
        }

        if (!changed)
        {
            return NotFound();
        }

        await wanted.ReconcileAsync(workId, cancellationToken);
        TempData["MangaWorkNotice"] = Ui["admin.manga.saved"];
        return RedirectToPage(new { workId });
    }

    /// <summary>
    /// Searches now: the open request that waits for a release is run again, the same action as Search now on Wanted and Requests; a Manga without an open request
    /// gets one through the shared access policy, for the whole title or for one volume or chapter, and is searched, downloaded and imported like any other.
    /// </summary>
    public async Task<IActionResult> OnPostSearchAsync(long workId, int? volume, double? chapter, CancellationToken cancellationToken)
    {
        if (!await ModuleEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (await query.GetAsync(workId, cancellationToken) is not { } view)
        {
            return NotFound();
        }

        if (await query.IdentityAsync(workId, cancellationToken) is not var (provider, externalId))
        {
            TempData["MangaWorkError"] = Ui["admin.manga.noIdentity"];
            return RedirectToPage(new { workId });
        }

        try
        {
            if (await requestStore.FindOpenAsync(MediaAcquisitionKind.Manga, provider, externalId, cancellationToken) is { } open)
            {
                if (open.Status != AcquisitionRequestStatus.Approved)
                {
                    TempData["MangaWorkError"] = Ui["admin.manga.search.running"];
                    return RedirectToPage(new { workId });
                }

                var result = await requests.ApproveAsync(open.Id, cancellationToken);
                TempData["MangaWorkNotice"] = string.IsNullOrWhiteSpace(result.StatusMessage) ? result.Title : result.StatusMessage;
                return RedirectToPage(new { workId });
            }

            var payload = volume is null && chapter is null
                ? null
                : JsonSerializer.Serialize(new ReadingRequestPayload(view.Title, [], null, volume, chapter, chapter), JsonSerializerOptions.Web);
            var submission = await requests.SubmitWithOutcomeAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Manga, provider, externalId, view.Title, view.NativeTitle, null, payload) { WorkId = workId }, cancellationToken);
            TempData["MangaWorkNotice"] = submission.Request.StatusMessage ?? Ui["admin.manga.search.started"];
        }
        catch (AcquisitionAccessDeniedException)
        {
            return Forbid();
        }

        return RedirectToPage(new { workId });
    }

    /// <summary>Runs the failed request of this Manga again with the intent it was saved with, the same action as Retry on Requests.</summary>
    public async Task<IActionResult> OnPostRetryAsync(long workId, CancellationToken cancellationToken)
    {
        if (!await ModuleEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (await query.GetAsync(workId, cancellationToken) is not { Request: { Status: AcquisitionRequestStatus.Failed } failed })
        {
            TempData["MangaWorkError"] = Ui["admin.manga.retry.none"];
            return RedirectToPage(new { workId });
        }

        try
        {
            var outcome = await requests.RetryAsync(failed.Id, cancellationToken);
            TempData[outcome == RequestRetryOutcome.NotRetryable ? "MangaWorkError" : "MangaWorkNotice"] = Ui[outcome == RequestRetryOutcome.NotRetryable ? "admin.manga.retry.refused" : "admin.manga.retry.started"];
        }
        catch (AcquisitionAccessDeniedException)
        {
            return Forbid();
        }

        return RedirectToPage(new { workId });
    }

    /// <summary>Assigns the quality profile of this Manga; a blank profile returns to the default of the media type.</summary>
    public async Task<IActionResult> OnPostProfileAsync(long workId, string? profileId, CancellationToken cancellationToken)
    {
        if (!await ModuleEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (await query.GetAsync(workId, cancellationToken) is null)
        {
            return NotFound();
        }

        if (!string.IsNullOrWhiteSpace(profileId) && !(await profiles.LoadAsync(cancellationToken)).Profiles.Any(profile => profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase) && MangaWorkAdminQuery.ServesManga(profile)))
        {
            TempData["MangaWorkError"] = Ui["admin.manga.profile.unknown"];
            return RedirectToPage(new { workId });
        }

        await profiles.AssignWorkAsync(workId, profileId, cancellationToken);
        await wanted.ReconcileAsync(workId, cancellationToken);
        TempData["MangaWorkNotice"] = Ui["admin.manga.profile.saved"];
        return RedirectToPage(new { workId });
    }

    /// <summary>Asks AniList again for the volumes and chapters it states: units that became known are added, nothing the Work holds is removed or renumbered.</summary>
    public async Task<IActionResult> OnPostRefreshAsync(long workId, CancellationToken cancellationToken)
    {
        if (!await ModuleEnabledAsync(cancellationToken))
        {
            return NotFound();
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (await query.GetAsync(workId, cancellationToken) is null)
        {
            return NotFound();
        }

        var refresh = await structure.RefreshAsync(workId, cancellationToken);
        if (refresh.Problem is { } problem)
        {
            TempData["MangaWorkError"] = problem;
        }
        else
        {
            var added = (refresh.Volumes?.Created ?? 0) + (refresh.Chapters?.Created ?? 0);
            TempData["MangaWorkNotice"] = added > 0 ? Ui.Format("admin.manga.refresh.added", ("count", added)) : Ui["admin.manga.refresh.same"];
        }

        return RedirectToPage(new { workId });
    }

    public string StateLabel(ReadingCoverageState state, bool wanted) =>
        Ui[state switch
        {
            ReadingCoverageState.Installed => "admin.manga.state.installed",
            ReadingCoverageState.Partial => "admin.manga.state.partial",
            _ => wanted ? "admin.manga.state.wanted" : "admin.manga.state.missing"
        }];

    public string RequestLabel(AcquisitionRequestStatus status) => Ui["requests.status." + AcquisitionAccessNames.Status(status)];

    /// <summary>Whether a library file can be read now (an unmounted share, a deleted file); asked only for the rows a page shows.</summary>
    public static bool Readable(string path) => path.Length > 0 && (System.IO.File.Exists(path) || Directory.Exists(path));

    private async Task<bool> ModuleEnabledAsync(CancellationToken cancellationToken) =>
        instanceModules is null || await instanceModules.IsEnabledAsync(AcquisitionInstanceModules.For(MediaAcquisitionKind.Manga), cancellationToken);
}

/// <summary>The switch, the search and the reset of one volume or chapter row.</summary>
/// <param name="Scope"><c>volume</c> or <c>chapter</c>.</param>
/// <param name="Decision">The owner's own decision on the unit; the reset shows only while there is one.</param>
public sealed record MangaUnitControls(long WorkId, string Scope, Guid Id, double Number, bool Monitored, bool? Decision, string Name, bool CanSearch, UiTextBundle Ui);

public sealed record MangaUnitRow(long WorkId, ReadingChapterUnit Chapter, UiTextBundle Ui, bool CanSearch);
