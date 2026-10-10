using System.Data;
using Npgsql;

namespace Jularr.Infrastructure.Sql;

public enum SqlAccessMode : byte
{
    ReadOnly,
    ReadWrite
}

public sealed class SqlContext : IAsyncDisposable
{
    private readonly NpgsqlDataSource _database;
    private readonly SqlLogicCommands _logicSql;
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;
    private SqlAccessMode? _mode;
    private bool _finished;
    private bool _disposed;

    public SqlReadCommands ReadSql { get; }

    public SqlContext(NpgsqlDataSource database)
    {
        _database = database;
        ReadSql = new SqlReadCommands(this);
        _logicSql = new SqlLogicCommands(this);
    }

    public async Task BeginAsync(SqlAccessMode mode, IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (_transaction is not null)
        {
            throw new InvalidOperationException("A SQL transaction has already been started.");
        }

        var connection = await _database.OpenConnectionAsync(cancellationToken);

        try
        {
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);

            try
            {
                if (mode == SqlAccessMode.ReadOnly)
                {
                    await using var command = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                _connection = connection;
                _transaction = transaction;
                _mode = mode;
            }
            catch
            {
                await transaction.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public SqlLogicCommands RequireLogicSql()
    {
        RequireActive();

        if (_mode != SqlAccessMode.ReadWrite)
        {
            throw new InvalidOperationException("The current SQL transaction is read-only.");
        }

        return _logicSql;
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        RequireActive();
        await _transaction!.CommitAsync(cancellationToken);
        _finished = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_transaction is not null)
            {
                await _transaction.DisposeAsync();
            }
        }
        finally
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }
        }
    }

    private void RequireActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_transaction is null || _finished)
        {
            throw new InvalidOperationException("A live SQL transaction is required.");
        }
    }

    private NpgsqlCommand CreateCommand(string sql, bool write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        RequireActive();

        if (write && _mode != SqlAccessMode.ReadWrite)
        {
            throw new InvalidOperationException("Write commands require a read-write transaction.");
        }

        return new NpgsqlCommand(sql, _connection!, _transaction);
    }

    public sealed class SqlReadCommands(SqlContext context)
    {
        public async Task<object?> ExecuteScalarAsync(string sql, CancellationToken cancellationToken = default)
        {
            await using var command = context.CreateCommand(sql, false);
            return await command.ExecuteScalarAsync(cancellationToken);
        }
    }

    public sealed class SqlLogicCommands(SqlContext context)
    {
        public async Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            await using var command = context.CreateCommand(sql, true);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
