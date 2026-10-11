namespace Jularr.Backend.Service.Core
{
    public sealed class AuthenticationOptions
    {
        public int SessionLifetimeHours { get; set; } = 24;
        public bool PlexLoginEnabled { get; set; }
        public bool PlexRegistrationEnabled { get; set; }
        public bool PlexRequireApproval { get; set; } = true;
        public long PlexUiLocaleId { get; set; } = 1;
        public Uri? PlexServerUri { get; set; }
        public string PlexServerIdentifier { get; set; } = "";
        public string[] PlexRegistrationLibraryKeys { get; set; } = [];
    }
}
