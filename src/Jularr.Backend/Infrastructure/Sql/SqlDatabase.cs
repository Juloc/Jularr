using Npgsql;

namespace Jularr.Backend.Infrastructure.Sql
{
    internal sealed class SqlDatabase(NpgsqlDataSource dataSource) : IDisposable, IAsyncDisposable
    {
        internal NpgsqlDataSource DataSource { get; } = dataSource;

        // Close the host-owned pool.
        public void Dispose() => DataSource.Dispose();

        public ValueTask DisposeAsync() => DataSource.DisposeAsync();
    }
}
