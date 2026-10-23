using Jularr.Backend.Service.Core.Localization;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Enums.Service;
using System.Reflection;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Public.Localization.V1;
using Jularr.Integrations.Logic.Plex;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ServiceArchitectureTests
    {
        [TestMethod]
        public void ConsumerBoundary_DatabaseAndRuntimeOwners_AreInternal()
        {
            var owners = typeof(ServiceApi).Assembly.GetTypes().Where(type =>
                type.Namespace?.StartsWith("Jularr.Backend.Logic.", StringComparison.Ordinal) == true
                || type.Namespace?.StartsWith("Jularr.Backend.Infrastructure.", StringComparison.Ordinal) == true);

            foreach (var type in owners.Concat([typeof(SqlParams), typeof(ServiceRuntime), typeof(IServiceGate), typeof(ServiceGate),
                typeof(SessionAuthentication), typeof(SessionCookieEvents), typeof(UiLocalizationCache), typeof(PlexClient), typeof(ISessionResult), typeof(IAuthenticationChallengeResult)]))
            {
                Assert.IsFalse(type.IsVisible, $"{type.FullName} is exposed to consumers.");
            }

            var friends = typeof(ServiceApi).Assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>();
            CollectionAssert.AreEquivalent(new[] { "Jularr.Backend.Tests" }, friends.Select(friend => friend.AssemblyName).ToArray());
        }

        [TestMethod]
        public void ErrorCodes_ThrownBySourceCode_HaveTheirEnglishText()
        {
            var keys = ServiceErrorResources.English.Select(resource => resource.Key).ToHashSet(StringComparer.Ordinal);
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root.GetFiles("Jularr.sln").Length == 0)
            {
                root = root.Parent!;
            }

            var missing = new SortedSet<string>(StringComparer.Ordinal);
            var files = new[] { "Jularr.Backend", "Jularr.Integrations" }.SelectMany(project => Directory.EnumerateFiles(Path.Combine(root.FullName, "src", project), "*.cs", SearchOption.AllDirectories))
                .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                foreach (Match code in Regex.Matches(text, @"new (?:ServiceUnavailableException|NasMountException|SynologyException)\(\s*""([a-z0-9_]+)""\s*\)"))
                {
                    if (!keys.Contains("errors." + code.Groups[1].Value)) missing.Add(code.Groups[1].Value);
                }

                foreach (Match code in Regex.Matches(text, @"new DataValidationException\([^;]*?,\s*""([a-z0-9_]+)""\s*\)"))
                {
                    if (!keys.Contains("errors.validation." + code.Groups[1].Value)) missing.Add("validation." + code.Groups[1].Value);
                }
            }

            Assert.AreEqual(0, missing.Count, "Missing error texts: " + string.Join(", ", missing));
        }

        [TestMethod]
        public void ConsumerContracts_PublicAndProtectedSignatures_DoNotExposeNpgsql()
        {
            foreach (var type in typeof(ServiceApi).Assembly.GetExportedTypes())
            {
                // Include externally derived consumers.
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Where(method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);
                var dependencies = methods.SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType))
                    .Concat(type.GetConstructors().SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType)))
                    .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(field => field.FieldType));

                foreach (var dependency in dependencies)
                {
                    Assert.IsFalse(ReferencesNamespace(dependency, "Npgsql"), $"{type.FullName} exposes {dependency.FullName}.");
                }
            }
        }

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
                    Assert.IsFalse(ReferencesNamespace(dependency, "Jularr.Backend.Service"), $"{type.FullName} depends on {dependency.FullName} from the Service layer.");
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

            // Commands accept SQL objects only.
            foreach (var facade in new[] { typeof(SqlContext.SqlReadCommands), typeof(SqlContext.SqlLogicCommands) })
            {
                var commands = facade.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(method => method.Name.EndsWith("Async"));
                foreach (var command in commands)
                {
                    Assert.AreEqual(typeof(SqlStatement), command.GetParameters()[0].ParameterType);
                    Assert.IsFalse(command.Name is "ReadForUpdateAsync" or "ExecuteReturningAsync");
                }
            }
        }

        [TestMethod]
        public void ServiceInputs_CanBeRebuiltWithNormalizedFields()
        {
            // Inputs are immutable or partial; immutable ones are rebuilt through a constructor over their properties.
            var inputs = typeof(Service.Core.Service).Assembly.GetTypes()
                .Where(type => !type.IsAbstract && typeof(Service.Core.Service).IsAssignableFrom(type))
                .SelectMany(type =>
                {
                    var current = type;
                    while (current is not null && !(current.IsGenericType && current.GetGenericArguments().Length == 2))
                    {
                        current = current.BaseType;
                    }

                    return current?.GetGenericArguments() ?? [];
                })
                .Where(type => typeof(BaseData).IsAssignableFrom(type) && BaseData.GetFields(type).Length > 0)
                .Distinct();
            foreach (var input in inputs)
            {
                var isPartial = input.GetCustomAttribute<System.Text.Json.Serialization.JsonConverterAttribute>()?.ConverterType is { IsGenericType: true } converter
                    && converter.GetGenericTypeDefinition() == typeof(PartialDataJsonConverter<>);
                Assert.IsTrue(isPartial || BaseData.GetNormalizingConstructor(input) is not null, input.Name);
            }
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

        private static bool ReferencesNamespace(Type type, string prefix)
        {
            if (type.Namespace is not null && type.Namespace.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }

            if (type.IsGenericType)
            {
                return type.GenericTypeArguments.Any(argument => ReferencesNamespace(argument, prefix));
            }

            if (type.HasElementType && type.GetElementType() is { } elementType)
            {
                return ReferencesNamespace(elementType, prefix);
            }

            return false;
        }
    }
}
