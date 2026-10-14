using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace Jularr.E2E.Tests
{
    // A fresh Jularr on loopback: its own database from the baselines and the built web host as a separate process.
    internal sealed class JularrInstance : IAsyncDisposable
    {
        // Applied in the order of a new installation.
        private static readonly string[] Baselines = ["Account", "Instance", "Media", "Acquisition", "Storage", "Library", "Playback"];
        private readonly string _adminConnection;
        private readonly string _database = "jularr_e2e_" + Guid.NewGuid().ToString("N");
        private readonly string _storage = Directory.CreateTempSubdirectory("jularr-e2e-").FullName;

        // Keys outlive a restart, so sign-ins do too.
        private readonly string _keys = Directory.CreateTempSubdirectory("jularr-e2e-keys-").FullName;
        private Process? _process;
        private bool _created;

        private readonly IReadOnlyDictionary<string, string> _settings;

        private JularrInstance(string adminConnection, IReadOnlyDictionary<string, string> settings)
        {
            _adminConnection = adminConnection;
            _settings = settings;
            Address = new Uri("http://127.0.0.1:" + FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture) + "/");
        }

        public Uri Address { get; }

        // Folders the test fills, removed with the instance.
        public string Storage => _storage;

        // The repository holding this test project.
        public static string Repository { get; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        // Settings are configuration keys such as "Providers:Tmdb".
        public static async Task<JularrInstance> StartAsync(IReadOnlyDictionary<string, string>? settings = null)
        {
            var admin = Environment.GetEnvironmentVariable("JULARR_TEST_DB");
            if (string.IsNullOrWhiteSpace(admin))
            {
                Unavailable("JULARR_TEST_DB is not configured.");
            }

            // Whatever was set up before a failure is taken down again.
            var instance = new JularrInstance(admin!, settings ?? new Dictionary<string, string>());
            try
            {
                await instance.CreateDatabaseAsync();
                instance.StartHost();
                await instance.WaitUntilReadyAsync();
                return instance;
            }
            catch
            {
                await instance.DisposeAsync();
                throw;
            }
        }

        // Locally a missing prerequisite skips the browser tests; in CI it fails them.
        public static void Unavailable(string reason)
        {
            if (Environment.GetEnvironmentVariable("CI") is not null)
            {
                Assert.Fail(reason);
            }

            Assert.Inconclusive(reason);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopHostAsync();

                if (_created)
                {
                    await using var connection = new NpgsqlConnection(_adminConnection);
                    await connection.OpenAsync();
                    await using var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS \"" + _database + "\" WITH (FORCE)", connection);
                    await drop.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                Directory.Delete(_storage, true);
                Directory.Delete(_keys, true);
            }
        }

        // Stops the host as a crash would and starts it again on the same database.
        public async Task RestartAsync()
        {
            await StopHostAsync();
            StartHost();
            await WaitUntilReadyAsync();
        }

        // Ends every connection to the instance's database, as a PostgreSQL restart does.
        public async Task DropConnectionsAsync()
        {
            await using var connection = new NpgsqlConnection(_adminConnection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = @database AND pid <> pg_backend_pid()", connection);
            command.Parameters.AddWithValue("database", _database);
            await command.ExecuteScalarAsync();
        }

        public async Task RunSqlAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        private async Task StopHostAsync()
        {
            if (_process is null)
            {
                return;
            }

            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            await _process.WaitForExitAsync();
            _process.Dispose();
            _process = null;
        }

        private string ConnectionString => new NpgsqlConnectionStringBuilder(_adminConnection) { Database = _database, Pooling = false }.ConnectionString;

        private async Task CreateDatabaseAsync()
        {
            await using (var connection = new NpgsqlConnection(_adminConnection))
            {
                await connection.OpenAsync();
                await using var create = new NpgsqlCommand("CREATE DATABASE \"" + _database + "\"", connection);
                await create.ExecuteNonQueryAsync();
                _created = true;
            }

            await using var database = new NpgsqlConnection(ConnectionString);
            await database.OpenAsync();
            foreach (var name in Baselines)
            {
                await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(Repository, "database", name + ".sql")), database);
                await command.ExecuteNonQueryAsync();
            }
        }

        // The host is the build next to this project's, in the same configuration.
        private void StartHost()
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var frontend = Path.Combine(Repository, "src", "Jularr.Frontend");
            var start = new ProcessStartInfo("dotnet", [Path.Combine(frontend, "bin", configuration, "net10.0", "Jularr.Frontend.dll")])
            {
                WorkingDirectory = frontend,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment["ASPNETCORE_URLS"] = Address.ToString().TrimEnd('/');
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["JULARR_DB"] = ConnectionString;
            start.Environment["Storage__MountRoot"] = _storage;
            start.Environment["Storage__RequireMountPoint"] = "false";
            start.Environment["DataProtection__KeyDirectory"] = _keys;
            foreach (var (key, value) in _settings)
            {
                start.Environment[key.Replace(":", "__", StringComparison.Ordinal)] = value;
            }

            _process = Process.Start(start) ?? throw new InvalidOperationException("The Jularr host did not start.");
            _process.OutputDataReceived += (_, line) => Output(line.Data);
            _process.ErrorDataReceived += (_, line) => Output(line.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        private async Task WaitUntilReadyAsync()
        {
            using var http = new HttpClient { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(5) };
            Exception? last = null;
            for (var attempt = 0; attempt < 120; attempt++)
            {
                if (_process!.HasExited)
                {
                    throw new InvalidOperationException("The Jularr host stopped with exit code " + _process.ExitCode + ".");
                }

                // Refused or slow answers are expected while the host starts.
                try
                {
                    using var response = await http.GetAsync("api/v1/auth");
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return;
                    }

                    last = new HttpRequestException("HTTP " + (int)response.StatusCode);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    last = exception;
                }

                await Task.Delay(500);
            }

            throw new TimeoutException("The Jularr host did not answer within a minute.", last);
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        // Host output joins the evidence, so a failure shows the server side too.
        private static void Output(string? line)
        {
            if (line is not null)
            {
                Evidence.Log("[jularr] " + line);
            }
        }
    }
}
