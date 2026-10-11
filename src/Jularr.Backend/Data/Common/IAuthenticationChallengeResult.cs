namespace Jularr.Backend.Data.Common
{
    public interface IAuthenticationChallengeResult : IServiceOutput
    {
        string BrowserNonce { get; }
        DateTime ExpiresAt { get; }
    }
}
