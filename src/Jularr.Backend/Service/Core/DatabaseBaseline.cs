using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jularr.Backend.Service.Core
{
    // A new installation: an empty database receives the fresh baselines in one transaction before anything else starts.
    // A database with any table of its own is left exactly as it is; nothing is migrated.
    internal sealed partial class DatabaseBaseline(string connectionString) : IHostedService
    {
        [GeneratedRegex(@"\A\s*BEGIN;|COMMIT;\s*\z")]
        private static partial Regex Wrapper();

        // The order of a new installation.
        public static readonly string[] Baselines = ["Account", "Instance", "Media", "Acquisition", "Storage", "Library", "Playback"];
        public static readonly string[] FreshMigrations = ["20261018_01_DiscoverySnapshots"];

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var empty = new NpgsqlCommand("SELECT NOT EXISTS (SELECT 1 FROM pg_catalog.pg_tables WHERE schemaname = current_schema());", connection, transaction))
            {
                if (!(bool)(await empty.ExecuteScalarAsync(cancellationToken))!)
                {
                    return;
                }
            }

            // Both ordinary baselines and versioned additions are installed only when empty.
            // A database that already has tables is never modified by this host.
            foreach (var resource in Baselines.Select(name => "Jularr.Database." + name + ".sql")
                .Concat(FreshMigrations.Select(name => "Jularr.Database.Migrations." + name + ".up.sql")))
            {
                using var reader = new StreamReader(typeof(DatabaseBaseline).Assembly.GetManifestResourceStream(resource)!);
                var text = await reader.ReadToEndAsync(cancellationToken);
                if (Wrapper().Count(text) != 2 || text.Split("COMMIT;").Length != 2)
                {
                    throw new InvalidDataException("The " + resource + " SQL resource must be exactly one BEGIN ... COMMIT block.");
                }

                var baseline = Wrapper().Replace(text, "");
                await using var command = new NpgsqlCommand(baseline, connection, transaction);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
