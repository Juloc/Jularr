using System.Security.Cryptography;
using System.Text;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Devices;
using Jularr.Web.Features.Plex;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("login")]
public sealed class PlexModel(
    AppDbContext db,
    OwnerAuthService accountAuth,
    MediaCapabilityStore capabilities,
    PlexAuthClient plex,
    SecurityEventLog securityEvents,
    IConfiguration configuration) : PageModel
{
    private const string NonceCookie = "Jularr.Plex.Flow";

    [BindProperty]
    public string UserName { get; set; } = string.Empty;

    [BindProperty]
    public string LocalPassword { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public Guid Flow { get; set; }

    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = "/";

    public bool HasVerifiedIdentity { get; private set; }
    public bool PlexLinked { get; private set; }
    public bool AutoProvisionEnabled =>
        configuration.GetValue<bool>("Plex:AutoProvisionEnabled");
    public bool CanUsePlex =>
        !string.IsNullOrWhiteSpace(ClientIdentifier)
        && (LoginEnabled || LinkEnabled);
    public string? MessageKey { get; private set; }

    private string ClientIdentifier =>
        configuration["Plex:ClientIdentifier"]?.Trim() ?? string.Empty;

    private bool LoginEnabled =>
        configuration.GetValue<bool>("Plex:LoginEnabled");

    public bool PlexLinkEnabled =>
        configuration.GetValue<bool>("Plex:LinkEnabled");

    private bool LinkEnabled => PlexLinkEnabled;

    public async Task<IActionResult> OnGetAsync(
        CancellationToken cancellationToken)
    {
        if (!await accountAuth.HasOwnerAsync(cancellationToken))
        {
            return RedirectToPage("/Account/Setup");
        }

        var accountId = OwnerAuthService.GetAccountId(User);
        PlexLinked = accountId is not null &&
            await db.AccountLoginIdentities.AsNoTracking().AnyAsync(
                x => x.AccountId == accountId && x.Provider == "plex",
                cancellationToken);
        var isLinkedAccount = accountId is not null;
        if (!PlexLinked &&
            (!CanUsePlex || isLinkedAccount && !LinkEnabled ||
                !isLinkedAccount && !LoginEnabled))
        {
            return NotFound();
        }

        ReturnUrl = SafeReturnUrl(ReturnUrl);
        return Page();
    }

    public async Task<IActionResult> OnPostStartAsync(
        CancellationToken cancellationToken)
    {
        if (!await accountAuth.HasOwnerAsync(cancellationToken))
        {
            return RedirectToPage("/Account/Setup");
        }

        if (!CanUsePlex)
        {
            return NotFound();
        }

        var currentAccountId = OwnerAuthService.GetAccountId(User);
        if (currentAccountId is null && !LoginEnabled ||
            currentAccountId is not null && !LinkEnabled)
        {
            return Forbid();
        }

        if (currentAccountId is not null)
        {
            var account = await accountAuth.ValidateCredentialsAsync(
                User.Identity?.Name ?? string.Empty,
                LocalPassword,
                cancellationToken);
            if (account?.Id != currentAccountId)
            {
                MessageKey = "account.plex.confirmLink";
                return Page();
            }
        }

        var scheme = Request.Scheme;
        if (scheme != Uri.UriSchemeHttps &&
            (HttpContext.Connection.RemoteIpAddress is not { } ip ||
                !System.Net.IPAddress.IsLoopback(ip)))
        {
            MessageKey = "account.plex.requireHttps";
            return Page();
        }

        var pin = await plex.CreatePinAsync(
            ClientIdentifier,
            cancellationToken);
        var nonce = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));
        var attempt = new PlexLoginAttempt
        {
            PinId = pin.Id,
            ClientIdentifier = ClientIdentifier,
            BrowserNonceHash = HashNonce(nonce),
            StartedAccountId = currentAccountId,
            ReturnPath = SafeReturnUrl(ReturnUrl),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
        };

        await db.PlexLoginAttempts
            .Where(x => x.ExpiresAtUtc < DateTime.UtcNow)
            .ExecuteDeleteAsync(cancellationToken);
        db.PlexLoginAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);

        Response.Cookies.Append(
            NonceCookie,
            nonce,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Expires = new DateTimeOffset(attempt.ExpiresAtUtc)
            });

        var callback = Url.Page(
            "/Account/Plex",
            pageHandler: "Finish",
            values: new { flow = attempt.Id },
            protocol: Request.Scheme);

        if (string.IsNullOrWhiteSpace(callback))
        {
            throw new InvalidOperationException(
                "Unable to construct the Plex callback URL.");
        }

        var authUrl = "https://app.plex.tv/auth#?clientID="
            + Uri.EscapeDataString(ClientIdentifier)
            + "&code=" + Uri.EscapeDataString(pin.Code)
            + "&context%5Bdevice%5D%5Bproduct%5D=Jularr"
            + "&forwardUrl=" + Uri.EscapeDataString(callback);
        return Redirect(authUrl);
    }

    public async Task<IActionResult> OnGetFinishAsync(
        CancellationToken cancellationToken)
    {
        if (!CanUsePlex)
        {
            return NotFound();
        }

        var attempt = await GetAttemptAsync(cancellationToken);
        if (attempt is null)
        {
            return BadRequest();
        }

        if (attempt.StartedAccountId is null && !LoginEnabled ||
            attempt.StartedAccountId is not null && !LinkEnabled)
        {
            return Forbid();
        }

        var currentAccountId = OwnerAuthService.GetAccountId(User);
        if (attempt.StartedAccountId is not null &&
            attempt.StartedAccountId != currentAccountId)
        {
            return Forbid();
        }

        if (attempt.VerifiedPlexAccountId is null)
        {
            string? plexAccountId;
            try
            {
                plexAccountId = await plex.ResolveAuthenticatedAccountIdAsync(
                    attempt.PinId,
                    attempt.ClientIdentifier,
                    cancellationToken);
            }
            catch (HttpRequestException)
            {
                MessageKey = "account.plex.unavailable";
                return Page();
            }

            if (plexAccountId is null)
            {
                MessageKey = "account.plex.pending";
                return Page();
            }

            attempt.VerifiedPlexAccountId = plexAccountId;
            await db.SaveChangesAsync(cancellationToken);
        }

        var linkedAccount = await accountAuth.GetByExternalIdentityAsync(
            "plex",
            attempt.VerifiedPlexAccountId,
            cancellationToken);
        if (linkedAccount is not null)
        {
            if (currentAccountId is not null &&
                currentAccountId != linkedAccount.Id)
            {
                MessageKey = "account.plex.alreadyLinked";
                return Page();
            }

            if (attempt.StartedAccountId is not null &&
                attempt.StartedAccountId != linkedAccount.Id)
            {
                MessageKey = "account.plex.alreadyLinked";
                return Page();
            }

            if (!await ConsumeAttemptAsync(attempt, cancellationToken))
            {
                return BadRequest();
            }
            if (attempt.StartedAccountId is null)
            {
                await SignInAsync(linkedAccount);
            }

            return LocalRedirect(attempt.ReturnPath);
        }

        var identityExists = await db.AccountLoginIdentities.AnyAsync(
            x => x.Provider == "plex"
                && x.ExternalAccountId == attempt.VerifiedPlexAccountId,
            cancellationToken);
        if (identityExists)
        {
            MessageKey = "account.plex.cannotSignIn";
            return Page();
        }

        HasVerifiedIdentity = true;
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(
        CancellationToken cancellationToken)
    {
        var attempt = await GetAttemptAsync(cancellationToken);
        if (attempt is null || attempt.VerifiedPlexAccountId is null)
        {
            return BadRequest();
        }

        if (!LoginEnabled || !AutoProvisionEnabled
            || attempt.StartedAccountId is not null
            || User.Identity?.IsAuthenticated == true)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(UserName) || UserName.Trim().Length > 80)
        {
            HasVerifiedIdentity = true;
            MessageKey = "account.plex.invalidName";
            return Page();
        }

        if (!await ConsumeAttemptAsync(attempt, cancellationToken))
        {
            return BadRequest();
        }

        OwnerAccount account;
        try
        {
            account = await accountAuth.CreateExternalAccountAsync(
                "plex",
                attempt.VerifiedPlexAccountId,
                UserName,
                isEnabled: false,
                cancellationToken);
            await capabilities.ConstrainNewExternalAccountAsync(
                account.Id,
                cancellationToken);

            if (!configuration.GetValue("Plex:RequireApproval", true))
            {
                await accountAuth.SetEnabledAsync(
                    account.Id,
                    true,
                    cancellationToken);
            }
        }
        catch (InvalidOperationException)
        {
            MessageKey = "account.plex.cannotCreate";
            return Page();
        }

        if (!account.IsEnabled)
        {
            MessageKey = "account.plex.awaiting";
            return Page();
        }

        await SignInAsync(account);
        return LocalRedirect(attempt.ReturnPath);
    }

    public async Task<IActionResult> OnGetLinkAsync(
        CancellationToken cancellationToken)
    {
        var attempt = await GetAttemptAsync(cancellationToken);
        if (attempt is null || attempt.VerifiedPlexAccountId is null
            || attempt.StartedAccountId is not null)
        {
            return BadRequest();
        }

        if (User.Identity?.IsAuthenticated != true)
        {
            var returnUrl = Url.Page(
                "/Account/Plex",
                pageHandler: "Link",
                values: new { flow = attempt.Id });
            return RedirectToPage(
                "/Account/Login",
                new { returnUrl });
        }

        HasVerifiedIdentity = true;
        return Page();
    }

    public async Task<IActionResult> OnPostLinkAsync(
        CancellationToken cancellationToken)
    {
        var attempt = await GetAttemptAsync(cancellationToken);
        if (attempt is null || attempt.VerifiedPlexAccountId is null)
        {
            return BadRequest();
        }

        var accountId = OwnerAuthService.GetAccountId(User);
        if (accountId is null || !LinkEnabled ||
            attempt.StartedAccountId is not null &&
            attempt.StartedAccountId != accountId)
        {
            return Forbid();
        }

        var account = await accountAuth.ValidateCredentialsAsync(
            User.Identity?.Name ?? string.Empty,
            LocalPassword,
            cancellationToken);
        if (account?.Id != accountId)
        {
            HasVerifiedIdentity = true;
            MessageKey = "account.plex.confirmExisting";
            return Page();
        }

        if (!await ConsumeAttemptAsync(attempt, cancellationToken))
        {
            return BadRequest();
        }

        try
        {
            await accountAuth.LinkExternalIdentityAsync(
                accountId,
                "plex",
                attempt.VerifiedPlexAccountId,
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            MessageKey = "account.plex.cannotLink";
            return Page();
        }

        return LocalRedirect(attempt.ReturnPath);
    }

    public async Task<IActionResult> OnPostUnlinkAsync(
        CancellationToken cancellationToken)
    {
        var accountId = OwnerAuthService.GetAccountId(User);
        if (accountId is null)
        {
            return Forbid();
        }

        var account = await accountAuth.ValidateCredentialsAsync(
            User.Identity?.Name ?? string.Empty,
            LocalPassword,
            cancellationToken);
        if (account?.Id != accountId)
        {
            PlexLinked = true;
            MessageKey = "account.plex.confirmRemove";
            return Page();
        }

        try
        {
            await accountAuth.UnlinkExternalIdentityAsync(
                accountId,
                "plex",
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            PlexLinked = true;
            MessageKey = "account.plex.cannotRemove";
            return Page();
        }

        return LocalRedirect("/");
    }

    private async Task<PlexLoginAttempt?> GetAttemptAsync(
        CancellationToken cancellationToken)
    {
        if (Flow == Guid.Empty ||
            !Request.Cookies.TryGetValue(NonceCookie, out var nonce) ||
            string.IsNullOrWhiteSpace(nonce))
        {
            return null;
        }

        var attempt = await db.PlexLoginAttempts.SingleOrDefaultAsync(
            x => x.Id == Flow && x.ExpiresAtUtc > DateTime.UtcNow,
            cancellationToken);
        if (attempt is null)
        {
            return null;
        }

        var expected = Convert.FromHexString(attempt.BrowserNonceHash);
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
        return CryptographicOperations.FixedTimeEquals(expected, actual)
            ? attempt
            : null;
    }

    private async Task<bool> ConsumeAttemptAsync(
        PlexLoginAttempt attempt,
        CancellationToken cancellationToken)
    {
        var deleted = await db.PlexLoginAttempts.Where(
            x => x.Id == attempt.Id &&
                x.VerifiedPlexAccountId != null &&
                x.ExpiresAtUtc > DateTime.UtcNow)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted != 1)
        {
            return false;
        }

        Response.Cookies.Delete(NonceCookie);
        return true;
    }

    private async Task SignInAsync(OwnerAccount account)
    {
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            OwnerAuthService.CreatePrincipal(account),
            new AuthenticationProperties
            {
                AllowRefresh = true
            });

        securityEvents.Record(
            SecurityEventKind.LoginSucceeded,
            account.Id,
            account.UserName,
            HttpContext.Connection.RemoteIpAddress?.ToString());
    }

    private string SafeReturnUrl(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";

    private static string HashNonce(string nonce) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(nonce)));
}
