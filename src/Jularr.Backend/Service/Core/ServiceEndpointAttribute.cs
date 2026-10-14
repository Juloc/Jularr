namespace Jularr.Backend.Service.Core
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class ServiceEndpointAttribute(string method, string route) : Attribute
    {
        public string Method { get; } = method.ToUpperInvariant();
        public string Route { get; } = route;

        // A rate limit policy by remote address, beyond the one every public endpoint has.
        public string? RateLimit { get; init; }
    }
}
