global using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace Jularr.Backend.Tests
{
    [TestClass]
    public static class TestAssembly
    {
        // Test databases are dropped when the run ends, not only at process exit.
        [AssemblyCleanup]
        public static void Cleanup() => Infrastructure.TestPostgres.Cleanup();
    }
}
