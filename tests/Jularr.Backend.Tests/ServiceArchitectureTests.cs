using System.Reflection;
using Jularr.Infrastructure.Sql;
using Jularr.Service.Core;

namespace Jularr.Tests;

[TestClass]
public sealed class ServiceArchitectureTests
{
    [TestMethod]
    public void NeutralSqlInfrastructure_DoesNotDependOnServiceTypes()
    {
        var types = typeof(SqlContext).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(SqlContext).Namespace)
            .ToArray();

        Assert.IsTrue(types.Length > 0);

        foreach (var type in types)
        {
            foreach (var dependency in GetDependencies(type))
            {
                Assert.IsFalse(ReferencesService(dependency), $"{type.FullName} depends on {dependency.FullName} from the Service layer.");
            }
        }
    }

    [TestMethod]
    public void ReadSqlFacade_HasNoWriteCommandMethods()
    {
        var methodNames = typeof(SqlContext.SqlReadCommands)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToArray();

        Assert.IsFalse(methodNames.Contains("ExecuteAsync"));
        Assert.IsFalse(methodNames.Contains("ReadForUpdateAsync"));
        Assert.IsFalse(methodNames.Contains("ExecuteReturningAsync"));
    }

    [TestMethod]
    public void OperationType_UsesOnlyTheFiveApprovedKinds()
    {
        CollectionAssert.AreEqual(
            new[] { "Read", "Create", "Update", "Delete", "Execute" },
            Enum.GetNames<ServiceOperationType>());
    }

    private static IEnumerable<Type> GetDependencies(Type type)
    {
        if (type.BaseType is not null)
        {
            yield return type.BaseType;
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            yield return field.FieldType;
        }

        foreach (var constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            yield return property.PropertyType;
        }
    }

    private static bool ReferencesService(Type type)
    {
        if (type.Namespace is not null && type.Namespace.StartsWith("Jularr.Service", StringComparison.Ordinal))
        {
            return true;
        }

        if (type.IsGenericType)
        {
            return type.GenericTypeArguments.Any(ReferencesService);
        }

        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            return ReferencesService(elementType);
        }

        return false;
    }
}
