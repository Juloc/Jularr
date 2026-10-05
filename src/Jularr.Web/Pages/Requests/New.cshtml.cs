using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Requests;

/// <summary>
/// Requests an anime title with options (#597): the whole series, chosen seasons or chosen episodes, the
/// audio and subtitle language, and a quality profile the owner opened to requests. What happens next —
/// a request the owner approves or an immediately approved request — follows the profile's capability for
/// anime (the capability matrix) and the owner's auto-approval rules, exactly as for the Request dialog in
/// Discover; the action is always Request.
/// </summary>
public sealed class NewModel(
    AppDbContext db,
    CurrentAccountContext account,
    AcquisitionRequestService requests,
    AcquisitionRequestSettingsStore settings,
    QualityProfileStore qualityProfiles) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    /// <summary>The title being requested; these travel through the form as public catalog facts.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ExternalId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Title { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Subtitle { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? CoverImageUrl { get; set; }

    /// <summary>"series", "seasons" or "episodes".</summary>
    [BindProperty]
    public string Scope { get; set; } = "series";

    [BindProperty]
    public string? Seasons { get; set; }

    [BindProperty]
    public string? Episodes { get; set; }

    /// <summary>The season that plain episode numbers such as <c>1-6</c> belong to.</summary>
    [BindProperty]
    public int EpisodeSeason { get; set; } = 1;

    [BindProperty]
    public string? Audio { get; set; }

    [BindProperty]
    public string? Subtitles { get; set; }

    [BindProperty]
    public string? QualityProfileId { get; set; }

    public IReadOnlyList<QualityProfile> SelectableProfiles { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken) ?? Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (await LoadAsync(cancellationToken) is { } refused)
        {
            return refused;
        }

        if (!TryBuildOptions(out var options))
        {
            Error = Ui["requests.new.invalid"];
            return Page();
        }

        try
        {
            var request = await requests.SubmitAsync(
                new AcquisitionRequestDraft(
                    MediaAcquisitionKind.Anime,
                    AniListMetadataProvider.ProviderKey,
                    ExternalId!.Trim(),
                    Title!.Trim(),
                    string.IsNullOrWhiteSpace(Subtitle) ? null : Subtitle.Trim(),
                    string.IsNullOrWhiteSpace(CoverImageUrl) ? null : CoverImageUrl.Trim(),
                    Options: options),
                cancellationToken);
            TempData["Status"] = request.StatusMessage ?? Ui["requests.new.sent"];
            return RedirectToPage("/Requests/Index");
        }
        catch (AcquisitionAccessDeniedException)
        {
            return Forbid();
        }
        catch (ArgumentException)
        {
            Error = Ui["requests.new.invalid"];
            return Page();
        }
    }

    /// <summary>Checks the title and the profile's access; returns the response to give when the page cannot be shown.</summary>
    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!int.TryParse(ExternalId, out var id) || id <= 0 || string.IsNullOrWhiteSpace(Title))
        {
            return BadRequest();
        }

        var access = await requests.GetCapabilitiesAsync(MediaAcquisitionKind.Anime, cancellationToken);
        if (!access.CanRequest)
        {
            return Forbid();
        }

        // The cover comes through the form as a catalog fact; only web images are ever shown or stored.
        if (!Uri.TryCreate(CoverImageUrl, UriKind.Absolute, out var cover) || cover.Scheme is not ("http" or "https"))
        {
            CoverImageUrl = null;
        }

        var profiles = (await qualityProfiles.LoadAsync(cancellationToken)).Profiles;
        var opened = (await settings.LoadAsync(cancellationToken)).RequesterQualityProfileIds;
        SelectableProfiles = account.Can(JularrPolicies.AdminMedia)
            ? profiles
            : [.. profiles.Where(profile => opened.Contains(profile.Id, StringComparer.Ordinal))];
        return null;
    }

    private bool TryBuildOptions(out AcquisitionRequestOptions options)
    {
        options = AcquisitionRequestOptions.Default;
        var built = new AcquisitionRequestOptions
        {
            AudioLanguage = Audio,
            SubtitleLanguage = Subtitles,
            QualityProfileId = QualityProfileId
        };

        switch (Scope)
        {
            case "series":
                break;

            case "seasons":
                if (!RequestSelectionText.TryParseSeasons(Seasons, out var seasons))
                {
                    return false;
                }

                built = built with { Scope = RequestScope.Seasons, Seasons = seasons };
                break;

            case "episodes":
                if (!RequestSelectionText.TryParseEpisodes(Episodes, EpisodeSeason, out var episodes))
                {
                    return false;
                }

                built = built with { Scope = RequestScope.Episodes, Episodes = episodes };
                break;

            default:
                return false;
        }

        options = built;
        return true;
    }
}
