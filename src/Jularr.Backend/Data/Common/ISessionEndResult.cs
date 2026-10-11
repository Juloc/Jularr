namespace Jularr.Backend.Data.Common
{
    internal interface ISessionEndResult : IServiceOutput
    {
        bool EndsCurrentSession { get; }
    }
}
