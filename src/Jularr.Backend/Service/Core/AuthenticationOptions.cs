namespace Jularr.Backend.Service.Core
{
    public sealed class AuthenticationOptions
    {
        public int SessionLifetimeHours { get; set; } = 720;
    }
}
