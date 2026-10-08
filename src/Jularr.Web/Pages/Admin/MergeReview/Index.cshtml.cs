using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin.MergeReview;

/// <summary>
/// Owner-gated Merge/Duplicate Review Center for the universal media core (#437, epic #556). Surfaces
/// library-wide duplicate suggestions and flagged identity conflicts and drives the #432 workflow —
/// manual merge of two works (progress/notes/wanted/collections follow the bridged legacy records),
/// split/reassign of a provider identity, and the #435 field-source pin — all through
/// <see cref="WorkService"/> and <see cref="WorkQueryService"/>. Every change lands in the durable
/// identity-change log shown at the bottom. Status is reported through the single layout toast.
/// </summary>
[Authorize(Policy = JularrPolicies.MappingEdit)]
public sealed class IndexModel(
    AppDbContext db,
    WorkService works,
    WorkQueryService query,
    LibraryWorkBackfill libraryWorks,
    CurrentAccountContext account) : PageModel
{
    private const int ListLimit = 50;

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public IReadOnlyList<WorkDuplicateSuggestion> Suggestions { get; private set; } = [];
    public IReadOnlyList<WorkConflictView> Conflicts { get; private set; } = [];
    public IReadOnlyList<WorkIdentityChangeView> History { get; private set; } = [];
    public IReadOnlyList<LibraryWorkReviewItem> LibraryReview { get; private set; } = [];

    // Selected-work context (only populated when WorkId is set and resolves).
    public WorkSummary? Selected { get; private set; }
    public IReadOnlyList<WorkFieldProvenance> Provenance { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public Guid? WorkId { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostMergeAsync(Guid keepWorkId, Guid mergeWorkId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        if (keepWorkId == Guid.Empty || mergeWorkId == Guid.Empty || keepWorkId == mergeWorkId)
        {
            TempData["Status"] = Ui["admin.mergeReview.status.notFound"];
            return RedirectToPage();
        }

        await works.MergeWorksAsync(keepWorkId, mergeWorkId, account.ProfileId, cancellationToken);
        TempData["Status"] = Ui["admin.mergeReview.status.merged"];
        return RedirectToPage(new { workId = keepWorkId });
    }

    public async Task<IActionResult> OnPostLinkEntryAsync(Guid entryId, string? targetWorkId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        if (!Guid.TryParse(targetWorkId?.Trim(), out var target) || target == Guid.Empty)
        {
            TempData["Status"] = Ui["admin.mergeReview.status.invalidTarget"];
            return RedirectToPage();
        }

        TempData["Status"] = Ui[StatusKey(await libraryWorks.LinkToWorkAsync(entryId, target, cancellationToken))];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateEntryWorkAsync(Guid entryId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        TempData["Status"] = Ui[StatusKey((await libraryWorks.CreateWorkAsync(entryId, cancellationToken)).Resolution)];
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRunBackfillAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        var result = await libraryWorks.RunAsync(cancellationToken);
        TempData["Status"] = Ui.Format("admin.mergeReview.library.status.backfilled", ("bound", result.Bound), ("created", result.Created), ("review", result.Review));
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostConfirmIdentityAsync(Guid identityId, Guid? workId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        var confirmed = await works.SetExternalIdentityReviewStateAsync(
            identityId, MappingReviewState.Confirmed, cancellationToken);
        TempData["Status"] = confirmed
            ? Ui["admin.mergeReview.status.confirmed"]
            : Ui["admin.mergeReview.status.notFound"];
        return RedirectToPage(new { workId });
    }

    public async Task<IActionResult> OnPostReassignAsync(
        int mediaType,
        string provider,
        string externalId,
        string? targetWorkId,
        Guid? workId,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        if (!Enum.IsDefined(typeof(WorkMediaType), mediaType))
        {
            TempData["Status"] = Ui["admin.mergeReview.status.notFound"];
            return RedirectToPage(new { workId });
        }

        if (!Guid.TryParse(targetWorkId, out var target) || target == Guid.Empty)
        {
            TempData["Status"] = Ui["admin.mergeReview.status.invalidTarget"];
            return RedirectToPage(new { workId });
        }

        var reassigned = await works.ReassignExternalIdentityAsync(
            (WorkMediaType)mediaType, provider, externalId, target, account.ProfileId,
            evidence: "owner reassignment", cancellationToken);
        TempData["Status"] = reassigned is null
            ? Ui["admin.mergeReview.status.notFound"]
            : Ui["admin.mergeReview.status.reassigned"];
        return RedirectToPage(new { workId = target });
    }

    public async Task<IActionResult> OnPostSplitAsync(
        int mediaType,
        string provider,
        string externalId,
        string? newTitle,
        Guid? workId,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        if (!Enum.IsDefined(typeof(WorkMediaType), mediaType))
        {
            TempData["Status"] = Ui["admin.mergeReview.status.notFound"];
            return RedirectToPage(new { workId });
        }

        var work = await works.SplitExternalIdentityToNewWorkAsync(
            (WorkMediaType)mediaType, provider, externalId, newTitle ?? "", account.ProfileId,
            evidence: "owner split", cancellationToken);
        TempData["Status"] = work is null
            ? Ui["admin.mergeReview.status.notFound"]
            : Ui["admin.mergeReview.status.split"];
        return RedirectToPage(new { workId = work?.Id ?? workId });
    }

    public async Task<IActionResult> OnPostPinFieldAsync(Guid workId, string fieldKey, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!account.Can(JularrPolicies.MappingEdit))
        {
            return Forbid();
        }

        if (workId != Guid.Empty && !string.IsNullOrWhiteSpace(fieldKey))
        {
            await works.SetManualFieldOverrideAsync(workId, fieldKey, cancellationToken);
            TempData["Status"] = Ui["admin.mergeReview.status.pinned"];
        }

        return RedirectToPage(new { workId });
    }

    private static string StatusKey(LibraryWorkResolution resolution) => resolution switch
    {
        LibraryWorkResolution.Linked => "admin.mergeReview.library.status.linked",
        LibraryWorkResolution.Created => "admin.mergeReview.library.status.created",
        LibraryWorkResolution.AlreadyBound => "admin.mergeReview.library.status.alreadyBound",
        LibraryWorkResolution.WrongMediaType => "admin.mergeReview.library.status.wrongMediaType",
        LibraryWorkResolution.IdentityHeldByAnotherWork => "admin.mergeReview.library.status.heldByAnotherWork",
        LibraryWorkResolution.WorkRepresentsAnotherEntry => "admin.mergeReview.library.status.representsAnotherEntry",
        _ => "admin.mergeReview.status.notFound"
    };

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        LibraryReview = await libraryWorks.ListReviewAsync(ListLimit, cancellationToken);
        Suggestions = await query.FindDuplicateSuggestionsAsync(ListLimit, cancellationToken);
        Conflicts = await query.ListConflictIdentitiesAsync(ListLimit, cancellationToken);

        if (WorkId is { } id)
        {
            Selected = await query.GetSummaryAsync(id, cancellationToken);
            if (Selected is not null)
            {
                Provenance = await query.GetProvenanceAsync(id, cancellationToken);
            }
        }

        History = await query.GetIdentityChangesAsync(Selected?.Id, ListLimit, cancellationToken);
    }
}
