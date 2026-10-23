namespace Jularr.Backend.Data.Common
{
    internal interface IAuthenticationChallengeResult : IServiceOutput
    {
        string BrowserNonce { get; }
        DateTime ExpiresAt { get; }
    }
}
