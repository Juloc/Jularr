namespace Jularr.Web.Features.Auth;

public sealed class PlexLoginAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long PinId { get; set; }
    public string ClientIdentifier { get; set; } = string.Empty;
    public string BrowserNonceHash { get; set; } = string.Empty;
    public string? StartedAccountId { get; set; }
    public string? VerifiedPlexAccountId { get; set; }
    // "login" and "media" use the same browser-bound Plex PIN lifecycle.
    // Never allow a media-consent flow to create or link a Jularr Account.
    public string Purpose { get; set; } = "login";
    public string ReturnPath { get; set; } = "/";
    public DateTime ExpiresAtUtc { get; set; }
}
