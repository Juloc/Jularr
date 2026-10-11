using System.Text.Json.Serialization;

namespace Jularr.Integrations.Plex
{
    public sealed class PlexIdentity
    {
        public PlexIdentity(string externalAccountId, string email, string displayName, bool emailVerified, string accessToken)
        {
            ExternalAccountId = externalAccountId;
            Email = email;
            DisplayName = displayName;
            EmailVerified = emailVerified;
            AccessToken = accessToken;
        }

        public string ExternalAccountId { get; }
        public string Email { get; }
        public string DisplayName { get; }
        public bool EmailVerified { get; }

        [JsonIgnore]
        public string AccessToken { get; }
    }
}
