using Jularr.Backend.Data.Logic.Session;

namespace Jularr.Backend.Data.Logic.Authentication
{
    public sealed class PlexCompletionData
    {
        public PlexCompletionData(SessionCredentialsData? session, bool requiresApproval)
        {
            Session = session;
            RequiresApproval = requiresApproval;
        }

        public SessionCredentialsData? Session { get; }
        public bool RequiresApproval { get; }
    }
}
