using System.Globalization;
using Npgsql;

namespace Jularr.Backend.Tests.Infrastructure
{
    public static class TestPostgres
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, string> Databases = new(StringComparer.Ordinal);
        private static readonly HashSet<string> Copies = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Templates = new(StringComparer.Ordinal);
        private static readonly SemaphoreSlim TemplateBuild = new(1, 1);
        private static string? _maintenance;
        private static bool _cleanupRegistered;

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

                var connectionString = Create(null);
                Databases.Add(key, connectionString);
                return connectionString;
            }
        }

        // A new database copied from a template that build fills once per test process (the schema and rows every copy starts with).
        // Copying a finished database is much cheaper than installing the baselines and migrations again for each test.
        public static async Task<string> CopyAsync(string template, Func<string, Task> build)
        {
            var source = await TemplateAsync(template, build);
            lock (Gate)
            {
                var connectionString = Create(source);
                Copies.Add(new NpgsqlConnectionStringBuilder(connectionString).Database!);
                return connectionString;
            }
        }

        // Drops a copy as soon as its test is done with it, ending what is still connected; a copy that cannot be dropped now is left to the cleanup.
        public static async Task DropAsync(string connectionString)
        {
            var name = new NpgsqlConnectionStringBuilder(connectionString).Database!;
            string maintenance;
            lock (Gate)
            {
                if (_maintenance is null || !Copies.Remove(name))
                {
                    return;
                }

                maintenance = _maintenance;
            }

            try
            {
                await using var connection = new NpgsqlConnection(maintenance);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection) { CommandTimeout = 120 };
                await command.ExecuteNonQueryAsync();
            }
            catch (NpgsqlException)
            {
                lock (Gate)
                {
                    Copies.Add(name);
                }
            }
        }

        // Runs at the end of the test assembly and again, harmlessly, at process exit.
        public static void Cleanup()
        {
            lock (Gate)
            {
                if (_maintenance is null || Databases.Count == 0 && Copies.Count == 0 && Templates.Count == 0)
                {
                    return;
                }

                NpgsqlConnection.ClearAllPools();
                using var connection = new NpgsqlConnection(_maintenance);
                connection.Open();
                foreach (var name in Databases.Values.Select(saved => new NpgsqlConnectionStringBuilder(saved).Database!).Concat(Copies).Concat(Templates.Values).ToArray())
                {
                    Drop(connection, name);
                }

                Databases.Clear();
                Copies.Clear();
                Templates.Clear();
            }
        }

        // The template's database name, built by the first test that needs it, one build at a time. A failed build is tried again by the next test.
        private static async Task<string> TemplateAsync(string template, Func<string, Task> build)
        {
            await TemplateBuild.WaitAsync();
            try
            {
                string connectionString;
                lock (Gate)
                {
                    if (Templates.TryGetValue(template, out var ready))
                    {
                        return ready;
                    }

                    connectionString = Create(null);
                    Copies.Add(new NpgsqlConnectionStringBuilder(connectionString).Database!);
                }

                try
                {
                    await build(connectionString);
                }
                catch
                {
                    await DropAsync(connectionString);
                    throw;
                }

                // A database is copied only while nobody is connected to it: the build has closed its pools, and nothing opens the template again.
                var name = new NpgsqlConnectionStringBuilder(connectionString).Database!;
                lock (Gate)
                {
                    Copies.Remove(name);
                    Templates.Add(template, name);
                }

                return name;
            }
            finally
            {
                TemplateBuild.Release();
            }
        }

        // Called under Gate: an empty database, or a copy of the template; the caller records it for the cleanup. Copies made with WAL_LOG need no checkpoint, which stays cheap while other tests write.
        private static string Create(string? template)
        {
            var options = Maintenance(out var first);
            var name = Prefix + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..12];
            using var connection = new NpgsqlConnection(_maintenance);
            connection.Open();
            if (first)
            {
                DropStale(connection);
            }

            var copy = template is null ? "" : $" TEMPLATE \"{template}\" STRATEGY WAL_LOG";
            using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"{copy}", connection) { CommandTimeout = 120 };
            command.ExecuteNonQuery();
            if (!_cleanupRegistered)
            {
                _cleanupRegistered = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
            }

            options.Database = name;
            return options.ConnectionString;
        }

        // The connection settings of the isolated test server; only the dedicated loopback server is accepted.
        private static NpgsqlConnectionStringBuilder Maintenance(out bool first)
        {
            var configured = Environment.GetEnvironmentVariable("JULARR_TEST_DB")
                ?? throw new InvalidOperationException("JULARR_TEST_DB must explicitly name the isolated test server.");
            var options = new NpgsqlConnectionStringBuilder(configured);
            if (options.Host != "127.0.0.1" || options.Port != 15433
                || options.Username != "jularr_test" || options.Database != "jularr_test_admin")
            {
                throw new InvalidOperationException("Tests require the dedicated loopback test server; application connections are rejected.");
            }

            // Plain connections close at once; a test that wants a pool builds a data source of its own, which it disposes.
            options.Pooling = false;
            options.Timeout = 5;
            options.CommandTimeout = 15;
            first = _maintenance is null;
            _maintenance ??= options.ConnectionString;
            return options;
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
