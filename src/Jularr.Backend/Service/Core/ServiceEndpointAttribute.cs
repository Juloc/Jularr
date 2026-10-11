namespace Jularr.Service.Core;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ServiceEndpointAttribute(string method, string route) : Attribute
{
    public string Method { get; } = method.ToUpperInvariant();
    public string Route { get; } = route;
}
