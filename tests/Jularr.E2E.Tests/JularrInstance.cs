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
        private readonly Process _process;
        private readonly string _adminConnection;
        private readonly string _database;
        private readonly string _storage;

        private JularrInstance(Process process, Uri address, string adminConnection, string database, string storage)
        {
            _process = process;
            Address = address;
            _adminConnection = adminConnection;
            _database = database;
            _storage = storage;
        }

        public Uri Address { get; }

        public static async Task<JularrInstance> StartAsync()
        {
            var admin = Environment.GetEnvironmentVariable("JULARR_TEST_DB");
            if (string.IsNullOrWhiteSpace(admin))
            {
                Assert.Inconclusive("JULARR_TEST_DB is not configured.");
            }

            var database = "jularr_e2e_" + Guid.NewGuid().ToString("N");
            await using (var connection = new NpgsqlConnection(admin))
            {
                await connection.OpenAsync();
                await using var create = new NpgsqlCommand("CREATE DATABASE \"" + database + "\"", connection);
                await create.ExecuteNonQueryAsync();
            }

            var connectionString = new NpgsqlConnectionStringBuilder(admin) { Database = database, Pooling = false }.ConnectionString;
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                foreach (var name in Baselines)
                {
                    await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(Repository, "database", name + ".sql")), connection);
                    await command.ExecuteNonQueryAsync();
                }
            }

            // The host is the build next to this project's, in the same configuration.
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var frontend = Path.Combine(Repository, "src", "Jularr.Frontend");
            var address = new Uri("http://127.0.0.1:" + FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture) + "/");
            var storage = Directory.CreateTempSubdirectory("jularr-e2e-").FullName;
            var start = new ProcessStartInfo("dotnet", [Path.Combine(frontend, "bin", configuration, "net10.0", "Jularr.Frontend.dll")])
            {
                WorkingDirectory = frontend,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment["ASPNETCORE_URLS"] = address.ToString().TrimEnd('/');
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["JULARR_DB"] = connectionString;
            start.Environment["Storage__MountRoot"] = storage;
            start.Environment["Storage__RequireMountPoint"] = "false";
            var process = Process.Start(start) ?? throw new InvalidOperationException("The Jularr host did not start.");
            process.OutputDataReceived += (_, line) => Output(line.Data);
            process.ErrorDataReceived += (_, line) => Output(line.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var instance = new JularrInstance(process, address, admin!, database, storage);
            await instance.WaitUntilReadyAsync();
            return instance;
        }

        // The repository holding this test project.
        public static string Repository { get; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }

            _process.Dispose();
            NpgsqlConnection.ClearAllPools();
            await using (var connection = new NpgsqlConnection(_adminConnection))
            {
                await connection.OpenAsync();
                await using var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS \"" + _database + "\" WITH (FORCE)", connection);
                await drop.ExecuteNonQueryAsync();
            }

            Directory.Delete(_storage, true);
        }

        private async Task WaitUntilReadyAsync()
        {
            using var http = new HttpClient { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(5) };
            for (var attempt = 0; attempt < 120; attempt++)
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException("The Jularr host stopped with exit code " + _process.ExitCode + ".");
                }

                try
                {
                    using var response = await http.GetAsync("api/v1/auth");
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                }
                catch (TaskCanceledException)
                {
                }

                await Task.Delay(500);
            }

            throw new TimeoutException("The Jularr host did not answer within a minute.");
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        // Host output joins the test output, so a failure shows the server side too.
        private static void Output(string? line)
        {
            if (line is not null)
            {
                Console.WriteLine("[jularr] " + line);
            }
        }
    }
}
