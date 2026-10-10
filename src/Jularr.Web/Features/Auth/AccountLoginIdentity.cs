namespace Jularr.Web.Features.Auth;

public sealed class AccountLoginIdentity
{
    public string AccountId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ExternalAccountId { get; set; } = string.Empty;
    public DateTime LinkedAtUtc { get; set; } = DateTime.UtcNow;
}
