using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Music;
using Jularr.Web.Features.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Music;

/// <summary>
/// One album: where it stands, its tracks and which of them are in the library, and, for an account that may request Music, the request of a
/// missing album through the shared request flow. There is no Play action: Jularr does not play music yet, and the page says only what is true.
/// </summary>
public sealed class AlbumModel(AppDbContext db, MusicQuery query, AcquisitionRequestService requests, IAuthorizationService authorization) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public MusicAlbumView? View { get; private set; }

    public bool CanRequest { get; private set; }

    public bool CanManage { get; private set; }

    public string? Notice => TempData["MusicNotice"] as string;

    public string? Error => TempData["MusicError"] as string;

    public async Task<IActionResult> OnGetAsync(Guid workId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        View = await query.GetAlbumAsync(workId, cancellationToken);
        if (View is null)
        {
            return NotFound();
        }

        // Server-side authorization stays with the request service; this only decides whether to show the button.
        CanRequest = View.Album.State is MusicAlbumState.Unmonitored or MusicAlbumState.Missing or MusicAlbumState.Failed
            && View.MusicBrainzReleaseGroupId is not null
            && (await requests.GetCapabilitiesAsync(MediaAcquisitionKind.Music, cancellationToken)).CanRequest;
        CanManage = (await authorization.AuthorizeAsync(User, JularrPolicies.AdminMedia)).Succeeded;
        return Page();
    }

    public async Task<IActionResult> OnPostRequestAsync(Guid workId, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var view = await query.GetAlbumAsync(workId, cancellationToken);
        if (view?.MusicBrainzReleaseGroupId is not { } groupId)
        {
            return NotFound();
        }

        var payload = new MusicRequestPayload(workId, view.Artist.Name, view.Album.Title, view.Album.Year);
        var draft = new AcquisitionRequestDraft(MediaAcquisitionKind.Music, ProviderKeys.MusicBrainz, groupId, view.Album.Title, view.Artist.Name, null, JsonSerializer.Serialize(payload, JsonSerializerOptions.Web));
        try
        {
            await requests.SubmitAsync(draft, cancellationToken);
            TempData["MusicNotice"] = Ui["music.requested"];
        }
        catch (AcquisitionAccessDeniedException)
        {
            TempData["MusicError"] = Ui["music.requestDenied"];
        }

        return RedirectToPage(new { workId });
    }

    public string StateLabel(MusicAlbumState state) => Ui[$"admin.music.state.{MusicAlbumPresentation.ForLibrary(state).ToString().ToLowerInvariant()}"];

    public string TypeLabel(MusicAlbumType type) => Ui[$"admin.music.type.{type.ToString().ToLowerInvariant()}"];
}
