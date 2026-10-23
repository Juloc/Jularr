using System.Globalization;
using Npgsql;

namespace Jularr.Backend.Tests.Infrastructure
{
    public static class TestPostgres
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, string> Databases = new(StringComparer.Ordinal);
        private static string? _maintenance;

        // Old runs that never reached their cleanup are removed before new databases are created.
        private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);
        private const string Prefix = "jularr_profile_tests_";

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
                var first = _maintenance is null;
                _maintenance ??= options.ConnectionString;
                var name = Prefix + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..12];
                using var connection = new NpgsqlConnection(_maintenance);
                connection.Open();
                if (first)
                {
                    DropStale(connection);
                }

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

        // Runs at the end of the test assembly and again, harmlessly, at process exit.
        public static void Cleanup()
        {
            lock (Gate)
            {
                if (_maintenance is null || Databases.Count == 0)
                {
                    return;
                }

                NpgsqlConnection.ClearAllPools();
                using var connection = new NpgsqlConnection(_maintenance);
                connection.Open();
                foreach (var saved in Databases.Values)
                {
                    Drop(connection, new NpgsqlConnectionStringBuilder(saved).Database!);
                }

                Databases.Clear();
            }
        }

        private static void DropStale(NpgsqlConnection connection)
        {
            // Names carry their creation time; databases without one are leftovers of the old naming.
            var names = new List<string>();
            using (var list = new NpgsqlCommand("SELECT datname FROM pg_database WHERE datname LIKE 'jularr\\_profile\\_tests\\_%'", connection))
            using (var reader = list.ExecuteReader())
            {
                while (reader.Read())
                {
                    names.Add(reader.GetString(0));
                }
            }

            foreach (var name in names)
            {
                var stamp = name[Prefix.Length..].Split('_')[0];
                var created = DateTime.TryParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
                    ? value
                    : DateTime.MinValue;
                if (DateTime.UtcNow - created > StaleAfter)
                {
                    Drop(connection, name);
                }
            }
        }

        // A slow drop never fails a run; a leftover is removed as stale later.
        private static void Drop(NpgsqlConnection connection, string name)
        {
            try
            {
                using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection) { CommandTimeout = 120 };
                command.ExecuteNonQuery();
            }
            catch (NpgsqlException)
            {
            }
        }
    }
}
