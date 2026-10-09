namespace Jularr.Web.Features.Auth;

public sealed class PlexLoginAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long PinId { get; set; }
    public string ClientIdentifier { get; set; } = string.Empty;
    public string BrowserNonceHash { get; set; } = string.Empty;
    public string? StartedAccountId { get; set; }
    public string? VerifiedPlexAccountId { get; set; }
    public string ReturnPath { get; set; } = "/";
    public DateTime ExpiresAtUtc { get; set; }
}
