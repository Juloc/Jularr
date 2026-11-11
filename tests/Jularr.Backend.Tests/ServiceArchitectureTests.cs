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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        public void Sql_ProfileWorksOfAnAsset_IsNotJoinedDirectly()
        {
            // Joined to an asset's Work, the view is planned apart and reads the catalog up to it (seconds with a large catalog).
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root.GetFiles("Jularr.sln").Length == 0)
            {
                root = root.Parent!;
            }

            var joined = Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "Jularr.Backend"), "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .Where(file => Regex.IsMatch(File.ReadAllText(file), @"JOIN ""ProfileWorks"" AS (\w+) ON \1\.""WorkId"" = asset\.""WorkId"""))
                .Select(Path.GetFileName).ToArray();
            Assert.AreEqual(0, joined.Length, "Use EXISTS or JOIN LATERAL (... OFFSET 0) in: " + string.Join(", ", joined));
        }

        [TestMethod]
        public void Sql_StoredJson_IsParsedOncePerRow()
        {
            // Inlined, the lateral parse runs again for every condition, sort key and block that reads it (seconds for a book's translations).
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root.GetFiles("Jularr.sln").Length == 0)
            {
                root = root.Parent!;
            }

            var inlined = Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "Jularr.Backend"), "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .Where(file => Regex.IsMatch(File.ReadAllText(file), @"SELECT convert_from\([^)]*\)::jsonb AS value\s*\)"))
                .Select(Path.GetFileName).ToArray();
            Assert.AreEqual(0, inlined.Length, "Parse stored JSON in a LATERAL (... OFFSET 0) in: " + string.Join(", ", inlined));
        }

        [TestMethod]
        public void HttpClients_ComeFromAddClientOrTheDirectAllowlist()
        {
            // A direct client has a handler of its own that DI cannot give it, and bounds its calls itself.
            var direct = new Dictionary<string, string>
            {
                ["VpnTunnels.cs"] = "asks for the public exit directly and through the tunnel process's own handler",
                ["TorrentJob.cs"] = "announces to trackers through the torrent route's handler",
                ["WireproxyTunnel.cs"] = "talks to the local wireproxy process on the loopback",
                ["BrowserPackages.cs"] = "installs the browser package for the browser host, which is built without DI",
                ["SourceBrowser.cs"] = "reads the local browser's debugging endpoint on the loopback"
            };

            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root.GetFiles("Jularr.sln").Length == 0)
            {
                root = root.Parent!;
            }

            var files = new[] { "Jularr.Backend", "Jularr.Integrations", "Jularr.Workers", "Jularr.Frontend" }
                .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root.FullName, "src", project), "*.cs", SearchOption.AllDirectories))
                .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToArray();
            var registrations = files.Sum(file => Regex.Matches(File.ReadAllText(file), @"\bAddHttpClient\b").Count);
            Assert.AreEqual(1, registrations, "Register external clients only through ServiceApi.AddClient, which gives them their call limits.");

            var unbounded = new List<string>();
            foreach (var file in files)
            {
                foreach (Match creation in Regex.Matches(File.ReadAllText(file), @"new HttpClient\b([^;]*);"))
                {
                    var bounded = creation.Groups[1].Value.Contains("CallLimitHandler", StringComparison.Ordinal)
                        || Regex.IsMatch(creation.Groups[1].Value, @"Timeout = (?!Timeout\.InfiniteTimeSpan)");
                    if (!direct.ContainsKey(Path.GetFileName(file)) || !bounded)
                    {
                        unbounded.Add(Path.GetFileName(file));
                    }
                }
            }

            Assert.AreEqual(0, unbounded.Count, "Use AddClient, or allow the direct client with a reason and a timeout: " + string.Join(", ", unbounded));
        }

        [TestMethod]
        public void ServicesWithOneTransaction_ReachOnlyGuardedExternalClients()
        {
            // Clients that talk to another machine past the transaction guard of AddClient; a service with one transaction may reach them for this reason.
            var unguarded = new Dictionary<Type, string>
            {
                [typeof(Jularr.Integrations.Logic.Usenet.UsenetDownloader)] = "NNTP: downloads run in Execute jobs",
                [typeof(Jularr.Integrations.Logic.Storage.SmbShares)] = "SMB: NAS file access is not HTTP and is not checked by this guard",
                [typeof(Jularr.Integrations.Logic.Codex.CodexCli)] = "Codex CLI: a local process that asks the AI service; translations run in Execute jobs",
                [typeof(VpnTunnels)] = "direct client through the tunnel process: VPN checks run in Execute services and jobs",
                [typeof(Jularr.Backend.Service.Admin.Wanted.V1.TorrentJob)] = "direct tracker client: torrent passes run in Execute jobs",
                [typeof(Jularr.Integrations.Logic.Network.WireproxyTunnel)] = "direct client to the local wireproxy process on the loopback",
                [typeof(Jularr.Integrations.Logic.Browser.BrowserPackages)] = "direct client that installs the browser package: runs in Execute services",
                [typeof(Jularr.Integrations.Logic.Browser.SourceBrowser)] = "direct client to the local browser on the loopback"
            };
            var external = new[] { typeof(HttpClient), typeof(System.Net.Sockets.Socket), typeof(System.Net.Sockets.TcpClient), typeof(System.Net.Sockets.NetworkStream) };

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JULARR_DB"] = "Host=127.0.0.1;Port=1;Database=none",
                ["DataPath"] = Path.Combine(Path.GetTempPath(), "jularr-tests", Guid.NewGuid().ToString("N"))
            }).Build();
            var registrations = new ServiceCollection().AddJularrServices(configuration);
            var guarded = registrations.Select(service => service.ServiceType).Where(TakesHttpClient).ToHashSet();
            var types = new[] { typeof(ServiceApi).Assembly, typeof(PlexClient).Assembly }.SelectMany(assembly => assembly.GetTypes())
                .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }).ToArray();
            var implementations = registrations.Where(service => !service.IsKeyedService).GroupBy(service => service.ServiceType)
                .ToDictionary(group => group.Key, group => group.Select(service => service.ImplementationType ?? service.ImplementationInstance?.GetType()).OfType<Type>().ToArray());

            var violations = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var service in types.Where(type => typeof(Service.Core.Service).IsAssignableFrom(type)))
            {
                var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(service);
                var operation = (ServiceOperationType)service.GetMethod("GetOperationType", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null)!;
                if (operation == ServiceOperationType.Execute)
                {
                    continue;
                }

                // Every type its constructor reaches through DI.
                var seen = new HashSet<Type>();
                var pending = new Stack<Type>([service]);
                while (pending.TryPop(out var type))
                {
                    if (!seen.Add(type))
                    {
                        continue;
                    }

                    var isExternal = TakesHttpClient(type) || type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Any(field => external.Contains(field.FieldType)
                        || field.FieldType.Namespace?.StartsWith("SMBLibrary", StringComparison.Ordinal) == true);
                    if (isExternal && !guarded.Contains(type) && !unguarded.ContainsKey(type))
                    {
                        violations.Add(service.Name + " -> " + type.Name);
                    }

                    foreach (var parameter in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SelectMany(constructor => constructor.GetParameters()))
                    {
                        var dependency = parameter.ParameterType;
                        if (dependency.IsGenericType && dependency.GetGenericTypeDefinition() is var definition
                            && (definition == typeof(IEnumerable<>) || definition == typeof(Func<>) || definition == typeof(Lazy<>)))
                        {
                            dependency = dependency.GenericTypeArguments[0];
                        }

                        if (dependency.Namespace?.StartsWith("Jularr.", StringComparison.Ordinal) != true)
                        {
                            continue;
                        }

                        var concrete = implementations.TryGetValue(dependency, out var registered) && registered.Length > 0 ? registered
                            : dependency.IsInterface || dependency.IsAbstract ? types.Where(dependency.IsAssignableFrom).ToArray() : [dependency];
                        foreach (var next in concrete)
                        {
                            pending.Push(next);
                        }
                    }
                }
            }

            Assert.AreEqual(0, violations.Count, "Register the client through AddClient, or allow it here with a reason: " + string.Join("; ", violations));
            Assert.IsTrue(unguarded.Values.All(reason => reason.Length > 0));

            static bool TakesHttpClient(Type type) => type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(HttpClient)));
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
