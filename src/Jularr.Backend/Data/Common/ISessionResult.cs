using Jularr.Backend.Data.Logic.Session;

namespace Jularr.Backend.Data.Common
{
    public interface ISessionResult : IServiceOutput
    {
        SessionCredentialsData? Session { get; }
    }
}
