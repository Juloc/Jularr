using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.ExternalPlayback.Plex;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Plex;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

public sealed record PlexAdminServerOption(
    string MachineIdentifier,
    string Name,
    Uri Endpoint,
    IReadOnlyList<PlexLibrarySection> Libraries);

[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class PlexModel(
    AppDbContext db,
    PlexIdentitySettingsStore provider,
    PlexProfileConnectionStore personal,
    PlexResourceDiscoveryClient discovery,
    PlexServerSelectionService serverSelection,
    PlexServerGrantStore grants,
    PlexCatalogReconciliationJobs reconciler) : PageModel
{
    [BindProperty]
    public string MachineIdentifier { get; set; } = string.Empty;

    [BindProperty]
    public string Endpoint { get; set; } = string.Empty;

    [BindProperty]
    public List<string> LibrarySectionIds { get; set; } = [];

    [BindProperty]
    public string SectionId { get; set; } = string.Empty;

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public bool MediaConnectionEnabled { get; private set; }
    public bool HasPersonalConnection { get; private set; }
    public bool DiscoveryFailed { get; private set; }
    public IReadOnlyList<PlexSelectedServer> Connected { get; private set; } = [];
    public IReadOnlyList<PlexAdminServerOption> Available { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(
            HttpContext, db);
        Connected = await grants.ListAsync(cancellationToken);

        var settings = await provider.GetAsync(cancellationToken);
        MediaConnectionEnabled = settings.CanConnectMedia;
        if (!MediaConnectionEnabled)
        {
            return;
        }

        var accountId = OwnerAuthService.GetAccountId(User);
        var status = accountId is null ? null
            : await personal.GetStatusAsync(accountId, cancellationToken);
        HasPersonalConnection = status?.IsUsable == true;
        if (!HasPersonalConnection)
        {
            return;
        }

        var accessToken = await personal.GetBackendTokenAsync(
            accountId!, cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return;
        }

        try
        {
            var found = await discovery.DiscoverAsync(
                accessToken, settings.ClientIdentifier,
                cancellationToken);

            var options = new List<PlexAdminServerOption>();
            foreach (var server in found.Where(x => x.Owned).Take(5))
            {
                var endpoint = server.Connections.FirstOrDefault()?.Url;
                if (endpoint is null)
                {
                    continue;
                }

                try
                {
                    var libraries = await serverSelection.GetAvailableLibrariesAsync(
                        User, server, endpoint, settings.ClientIdentifier,
                        cancellationToken);
                    if (libraries.Count > 0)
                    {
                        options.Add(new PlexAdminServerOption(
                            server.MachineIdentifier,
                            server.Name, endpoint, libraries));
                    }
                }
                catch (HttpRequestException)
                {
                    // A disconnected PMS must not hide otherwise available servers.
                }
            }

            Available = options;
        }
        catch (HttpRequestException)
        {
            DiscoveryFailed = true;
        }
    }

    public async Task<IActionResult> OnPostConnectAsync(
        CancellationToken cancellationToken)
    {
        var settings = await provider.GetAsync(cancellationToken);
        var accountId = OwnerAuthService.GetAccountId(User);
        if (!settings.CanConnectMedia || accountId is null)
        {
            return Forbid();
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var selectedUri) ||
            selectedUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(MachineIdentifier) ||
            LibrarySectionIds.Count is < 1 or > 100)
        {
            return BadRequest();
        }

        var token = await personal.GetBackendTokenAsync(
            accountId, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Forbid();
        }

        var resources = await discovery.DiscoverAsync(
            token, settings.ClientIdentifier, cancellationToken);
        var candidate = resources.FirstOrDefault(server =>
            server.Owned &&
            server.MachineIdentifier == MachineIdentifier &&
            server.Connections.Any(connection =>
                connection.Url == selectedUri));
        if (candidate is null)
        {
            return BadRequest();
        }

        try
        {
            await serverSelection.ApproveAsync(
                User, candidate, selectedUri,
                LibrarySectionIds, settings.ClientIdentifier,
                cancellationToken);
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }
        catch (InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status409Conflict);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(
        CancellationToken cancellationToken)
    {
        await serverSelection.RevokeAsync(
            User, MachineIdentifier, cancellationToken);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostScanAsync(
        CancellationToken cancellationToken)
    {
        var settings = await provider.GetAsync(cancellationToken);
        if (!settings.CanConnectMedia)
        {
            return Forbid();
        }

        await reconciler.QueueBatchAsync(
            User, MachineIdentifier, SectionId,
            settings.ClientIdentifier, 100, 2,
            restart: false, cancellationToken);
        return RedirectToPage();
    }
}
