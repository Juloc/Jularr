using Jularr.Backend.Data.Logic.Session;

namespace Jularr.Backend.Data.Common
{
    internal interface ISessionResult : IServiceOutput
    {
        SessionCredentialsData? Session { get; }
    }
}
