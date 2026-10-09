using System.Reflection;

namespace Jularr.Tests;

[TestClass]
public sealed class EndpointBindingNamesTests
{
    [TestMethod]
    public void NoServiceHasAnInstanceMethodNamedBindAsync()
    {
        var offenders = typeof(Program).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(method => method.Name == "BindAsync")
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToArray();

        Assert.IsEmpty(offenders, "Minimal API refuses to start when a type taken as an endpoint parameter has an instance BindAsync: " + string.Join(", ", offenders));
    }
}
