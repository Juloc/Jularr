using Npgsql;

namespace Jularr.Backend.Tests.Infrastructure
{
    public static class TestPostgres
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, string> Databases = new(StringComparer.Ordinal);
        private static string? _maintenance;

        public static string ResolveConnectionString(string key)
        {
            lock (Gate)
            {
                if (Databases.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                var configured = Environment.GetEnvironmentVariable("JULARR_TEST_DB")
                    ?? throw new InvalidOperationException("JULARR_TEST_DB must explicitly name the isolated test server.");
                var options = new NpgsqlConnectionStringBuilder(configured);
                if (options.Host != "127.0.0.1" || options.Port != 15433
                    || options.Username != "jularr_test" || options.Database != "jularr_test_admin")
                {
                    throw new InvalidOperationException("Tests require the dedicated loopback test server; application connections are rejected.");
                }

                options.Pooling = false;
                options.Timeout = 5;
                options.CommandTimeout = 15;
                _maintenance ??= options.ConnectionString;
                var name = "jularr_profile_tests_" + Guid.NewGuid().ToString("N");
                using var connection = new NpgsqlConnection(_maintenance);
                connection.Open();
                using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
                command.ExecuteNonQuery();
                options.Database = name;
                Databases.Add(key, options.ConnectionString);
                if (Databases.Count == 1)
                {
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
                }

                return options.ConnectionString;
            }
        }

        private static void Cleanup()
        {
            NpgsqlConnection.ClearAllPools();
            using var connection = new NpgsqlConnection(_maintenance);
            connection.Open();
            foreach (var saved in Databases.Values)
            {
                var name = new NpgsqlConnectionStringBuilder(saved).Database!;
                using var command = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", connection);
                command.ExecuteNonQuery();
            }
        }
    }
}
