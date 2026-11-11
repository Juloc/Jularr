using System.Globalization;
using Jularr.Backend.Data.Enums.Sql;
using Npgsql;

namespace Jularr.Backend.Infrastructure.Sql
{
    internal sealed class SqlDatabase(NpgsqlDataSource dataSource, SqlLane lane = SqlLane.Web) : IDisposable, IAsyncDisposable
    {
        // Npgsql gives up a little after the server's limit, so a statement past it ends with the server's own cancel.
        internal static readonly TimeSpan CommandBuffer = TimeSpan.FromSeconds(5);

        private int _inUse;
        private int _peak;

        internal NpgsqlDataSource DataSource { get; } = dataSource;

        // How the lane's connections appear in pg_stat_activity and in the log.
        internal string ApplicationName { get; } = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).ApplicationName is { Length: > 0 } name ? name : Name(lane);
        internal int PoolSize { get; } = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).MaxPoolSize;

        // A lane of its own: its name, its pool and the server's limits for its connections. Migrations, the backup and readiness use the raw connection string.
        internal static SqlDatabase Create(string connectionString, SqlLane lane, int poolSize, TimeSpan idleInTransaction, TimeSpan statementTimeout)
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            var options = builder.ConnectionStringBuilder;
            options.ApplicationName = Name(lane);
            options.MaxPoolSize = poolSize;
            options.CommandTimeout = CommandTimeout(statementTimeout);
            options.Options = (options.Options + " -c idle_in_transaction_session_timeout=" + Milliseconds(idleInTransaction) + " -c statement_timeout=" + Milliseconds(statementTimeout)).Trim();
            return new SqlDatabase(builder.Build(), lane);
        }

        internal static string Milliseconds(TimeSpan value) => ((long)value.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

        // Whole seconds above the server's limit.
        internal static int CommandTimeout(TimeSpan statementTimeout) => (int)Math.Ceiling((statementTimeout + CommandBuffer).TotalSeconds);

        private static string Name(SqlLane lane) => lane == SqlLane.Web ? "jularr-web" : "jularr-worker";

        internal async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
        {
            var connection = await DataSource.OpenConnectionAsync(cancellationToken);
            var inUse = Interlocked.Increment(ref _inUse);
            int peak;
            while (inUse > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, inUse, peak) != peak)
            {
            }

            return connection;
        }

        internal void Release() => Interlocked.Decrement(ref _inUse);

        // The most connections in use at once since the last call; the next count starts from those in use now.
        internal int TakePeak() => Interlocked.Exchange(ref _peak, Volatile.Read(ref _inUse));

        // Close the host-owned pool.
        public void Dispose() => DataSource.Dispose();

        public ValueTask DisposeAsync() => DataSource.DisposeAsync();
    }
}
