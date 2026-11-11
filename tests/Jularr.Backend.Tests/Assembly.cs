global using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace Jularr.Backend.Tests
{
    [TestClass]
    public static class TestAssembly
    {
        // Test hosts run as "Test", where development guards (such as no external call inside a transaction) fail at once.
        [AssemblyInitialize]
        public static void Initialize(TestContext context)
        {
            if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") is null && Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") is null)
            {
                Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Test");
            }
        }

        // Test databases are dropped when the run ends, not only at process exit.
        [AssemblyCleanup]
        public static void Cleanup() => Infrastructure.TestPostgres.Cleanup();
    }
}
