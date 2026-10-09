using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jularr.Tests.Infrastructure;

/// <summary>
/// Backs the whole test suite with real PostgreSQL (issue #570). The former SQLite tests each built
/// their own throwaway <c>.db</c> file; here every distinct SQLite-style "Data Source" a test builds
/// is mapped to its own PostgreSQL database, so the old file-per-test isolation is reproduced exactly
/// — including tests that open two independent databases at once.
///
/// A bounded pool of databases keeps disk use flat: new logical databases are cloned from a
/// once-migrated template until the pool is full, after which the least-recently-used one is reclaimed
/// by truncating it (no DROP, so no eviction stalls). MSTest runs serially, so a single lock guards
/// the pool. The base server comes from <c>JULARR_TEST_DB</c> (a throwaway ephemeral
/// <c>postgres:18</c> is the intended target); never point this at production data.
///
/// Isolation contract: every test process gets its own run id and therefore its own template
/// (<c>jt_{unix}_{rand}_tpl</c>) and databases (<c>jt_{unix}_{rand}_{n}</c>), so several
/// <c>dotnet test</c> runs can share one server concurrently. A run drops only its own databases at
/// process exit; leftovers of crashed runs are reclaimed by a later run once older than 12 hours.
/// </summary>
public static class TestPostgres
{
    private const int MaxLiveDatabases = 24;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> KeyToDatabase = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> Lru = new();
    private static bool _initialized;
    private static string _baseConnectionString = string.Empty;
    private static string[] _dataTables = [];
    private static string[] _templateTriggerObjects = [];
    private static int _counter;

    // Per-run prefix "jt_<unix seconds>_<random>_": unique per test process, so concurrent runs against
    // the same server never share (or drop) each other's template or databases, and the embedded
    // timestamp lets a later run recognise leftovers of crashed runs.
    private static readonly string RunId =
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "_" + Guid.NewGuid().ToString("N")[..8];
    private static readonly string DatabasePrefix = "jt_" + RunId + "_";
    private static readonly string TemplateDatabase = DatabasePrefix + "tpl";

    private static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("JULARR_TEST_DB")
        ?? "Host=localhost;Port=5433;Username=jularr;Password=devtest;Include Error Detail=true";

    /// <summary>
    /// Called by the <c>UseSqlite</c> test shim. Returns a PostgreSQL connection string for a database
    /// dedicated to the caller's logical database (its SQLite "Data Source" path); the same path always
    /// maps to the same database, distinct paths to distinct databases.
    /// </summary>
    public static string ResolveConnectionString(string? connectionString)
    {
        connectionString ??= "Data Source=:memory:";

        // A caller reusing an already-resolved PostgreSQL connection string (e.g. from
        // Database.GetConnectionString()) keeps using that same database.
        if (connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        var key = KeyFor(connectionString);
        lock (Gate)
        {
            EnsureInitialized();

            if (KeyToDatabase.TryGetValue(key, out var existing))
            {
                Touch(key);
                return ConnectionFor(existing);
            }

            string database;
            if (KeyToDatabase.Count >= MaxLiveDatabases && Lru.First is { } oldest)
            {
                // Reclaim the least-recently-used database by emptying it (cheaper and far more
                // reliable than DROP + CREATE under load).
                Lru.RemoveFirst();
                KeyToDatabase.Remove(oldest.Value, out database!);
                TruncateDatabase(database);
            }
            else
            {
                database = DatabasePrefix + Interlocked.Increment(ref _counter);
                CreateFromTemplate(database);
            }

            KeyToDatabase[key] = database;
            Lru.AddLast(key);
            return ConnectionFor(database);
        }
    }

    private static string KeyFor(string connectionString)
    {
        var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
        if (builder.TryGetValue("Data Source", out var value) && value is string ds && !string.IsNullOrWhiteSpace(ds))
        {
            return ds.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
                ? "mem-" + Guid.NewGuid().ToString("N")
                : ds;
        }

        return connectionString;
    }

    private static void Touch(string key)
    {
        var node = Lru.Find(key);
        if (node is not null)
        {
            Lru.Remove(node);
            Lru.AddLast(node);
        }
    }

    private static string ConnectionFor(string database) =>
        new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = database }.ConnectionString;

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _baseConnectionString = BaseConnectionString;
        var maintenance = new NpgsqlConnectionStringBuilder(_baseConnectionString) { Database = "postgres" }
            .ConnectionString;

        using (var admin = new NpgsqlConnection(maintenance))
        {
            admin.Open();
            // Only reclaim databases of long-dead runs; never touch another live run's databases.
            foreach (var stale in StaleDatabases(admin))
            {
                TryExecute(admin, $"DROP DATABASE IF EXISTS \"{stale}\" WITH (FORCE);");
            }

            Execute(admin, $"CREATE DATABASE \"{TemplateDatabase}\";");
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => DropRunDatabases();

        var templateConnection = new NpgsqlConnectionStringBuilder(_baseConnectionString) { Database = TemplateDatabase }
            .ConnectionString;
        using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                   .UseNpgsql(templateConnection).Options))
        {
            db.Database.Migrate();
        }

        _dataTables = LoadDataTables(templateConnection);
        _templateTriggerObjects = LoadTriggerObjects(templateConnection);
        NpgsqlConnection.ClearAllPools();
        _initialized = true;
    }

    private static void CreateFromTemplate(string database)
    {
        var maintenance = new NpgsqlConnectionStringBuilder(_baseConnectionString) { Database = "postgres" }
            .ConnectionString;
        using var admin = new NpgsqlConnection(maintenance);
        admin.Open();
        Execute(admin, $"CREATE DATABASE \"{database}\" TEMPLATE \"{TemplateDatabase}\";");
    }

    private static void TruncateDatabase(string database)
    {
        using var connection = new NpgsqlConnection(ConnectionFor(database));
        connection.Open();
        DropLeakedTriggers(connection);
        var list = string.Join(", ", _dataTables.Select(t => $"\"{t}\""));
        Execute(connection, $"TRUNCATE TABLE {list} RESTART IDENTITY CASCADE;");
    }

    // TRUNCATE neither fires nor removes triggers: a test that installed one (to make a write fail) must not poison the next test that gets this database.
    // Only what the migrated template does not have is dropped, so the schema's own triggers stay.
    private static void DropLeakedTriggers(NpgsqlConnection connection)
    {
        var leaked = LoadTriggerObjects(connection).Where(item => !_templateTriggerObjects.Contains(item, StringComparer.Ordinal)).ToArray();
        foreach (var item in leaked.Where(item => item.StartsWith("trigger|", StringComparison.Ordinal)))
        {
            var parts = item.Split('|');
            Execute(connection, $"DROP TRIGGER IF EXISTS \"{parts[2]}\" ON \"{parts[1]}\";");
        }

        foreach (var item in leaked.Where(item => item.StartsWith("function|", StringComparison.Ordinal)))
        {
            Execute(connection, $"DROP FUNCTION IF EXISTS \"{item.Split('|')[1]}\"() CASCADE;");
        }
    }

    private static string[] LoadTriggerObjects(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return LoadTriggerObjects(connection);
    }

    private static string[] LoadTriggerObjects(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 'trigger|' || c.relname || '|' || t.tgname
            FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE NOT t.tgisinternal AND n.nspname = 'public'
            UNION ALL
            SELECT 'function|' || p.proname
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'public' AND p.prorettype = 'trigger'::regtype;
            """;
        var items = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(reader.GetString(0));
        }

        return [.. items];
    }

    private static string[] LoadDataTables(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory';";
        var tables = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables.ToArray();
    }

    private static IReadOnlyList<string> StaleDatabases(NpgsqlConnection admin)
    {
        using var command = admin.CreateCommand();
        command.CommandText = "SELECT datname FROM pg_database WHERE datname LIKE 'jt\\_%';";
        var cutoff = DateTimeOffset.UtcNow.Subtract(StaleAfter).ToUnixTimeSeconds();
        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var parts = name.Split('_');
            if (parts.Length > 2 && long.TryParse(parts[1], out var created) && created < cutoff)
            {
                names.Add(name);
            }
        }

        return names;
    }

    internal static void DropRunDatabases()
    {
        try
        {
            if (!_initialized)
            {
                return;
            }

            NpgsqlConnection.ClearAllPools();
            var maintenance = new NpgsqlConnectionStringBuilder(_baseConnectionString) { Database = "postgres" }
                .ConnectionString;
            using var admin = new NpgsqlConnection(maintenance);
            admin.Open();
            foreach (var database in KeyToDatabase.Values.Append(TemplateDatabase).ToArray())
            {
                TryExecute(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE);");
            }
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
        {
        }
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        command.ExecuteNonQuery();
    }

    private static void TryExecute(NpgsqlConnection connection, string sql)
    {
        try
        {
            Execute(connection, sql);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
        }
    }
}
