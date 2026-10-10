using System.Data;
using Jularr.Web.Data;
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
    private long? _actorAccountId;
    private long? _activeProfileId;
    private bool _scopeInitialized;
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

    internal void SetAuthorizedScope(long? actorAccountId, long? activeProfileId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_scopeInitialized)
        {
            throw new InvalidOperationException("The verified SQL caller scope cannot be replaced.");
        }

        if (actorAccountId is <= 0 || activeProfileId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actorAccountId));
        }

        _actorAccountId = actorAccountId;
        _activeProfileId = activeProfileId;
        _scopeInitialized = true;
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

    private NpgsqlCommand CreateCommand(string sql, bool write, object? parameters = null, object? data = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        RequireActive();

        if (write && _mode != SqlAccessMode.ReadWrite)
        {
            throw new InvalidOperationException("Write commands require a read-write transaction.");
        }

        var command = new NpgsqlCommand(sql, _connection!, _transaction);
        try
        {
            if (parameters is not null)
            {
                command.Parameters.AddRange(SqlExecutor.Bind(sql, parameters, data, _actorAccountId, _activeProfileId));
            }
            else if (data is not null)
            {
                throw new InvalidOperationException("An SQL data object requires a parameters object.");
            }
            else if (sql.Contains('@'))
            {
                command.Parameters.AddRange(SqlExecutor.Bind(sql, new NoSqlParameters(), null, _actorAccountId, _activeProfileId));
            }

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    private sealed record NoSqlParameters;

    public sealed class SqlReadCommands(SqlContext context)
    {
        public async Task<object?> ExecuteScalarAsync(string sql, CancellationToken cancellationToken = default)
        {
            await using var command = context.CreateCommand(sql, false);
            return await command.ExecuteScalarAsync(cancellationToken);
        }

        public async Task<object?> ExecuteScalarAsync(string sql, object parameters, CancellationToken cancellationToken = default)
        {
            await using var command = context.CreateCommand(sql, false, parameters);
            return await command.ExecuteScalarAsync(cancellationToken);
        }

        public async Task<T?> ReadOptionalAsync<T>(string sql, object parameters, CancellationToken cancellationToken = default)
        {
            await using var command = context.CreateCommand(sql, false, parameters);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                return default;
            }

            var result = SqlExecutor.MapRow<T>(reader);
            if (await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("A required single-row query returned multiple results.");
            }

            return result;
        }

        public async Task<T> ReadRequiredAsync<T>(string sql, object parameters, CancellationToken cancellationToken = default)
        {
            var result = await ReadOptionalAsync<T>(sql, parameters, cancellationToken);
            return result is null ? throw new KeyNotFoundException("The required database resource was not found.") : result;
        }

        public async Task<PageResult<T>> ReadPageAsync<T>(string sql, object parameters, CancellationToken cancellationToken = default)
        {
            var request = SqlExecutor.GetPageRequest(parameters);
            await using var command = context.CreateCommand(sql, false, parameters);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken);
            var items = new List<T>();

            while (await reader.ReadAsync(cancellationToken))
            {
                if (items.Count >= request.PageSize)
                {
                    throw new InvalidOperationException("The paged SQL query exceeded its declared PageSize.");
                }

                items.Add(SqlExecutor.MapRow<T>(reader));
            }

            return PageResult<T>.From(items, request);
        }
    }

    public sealed class SqlLogicCommands(SqlContext context)
    {
        public async Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            await using var command = context.CreateCommand(sql, true);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<int> ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken = default)
        {
            await using var command = context.CreateCommand(sql, true, parameters);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<int> ExecuteAsync(string sql, object parameters, object data, CancellationToken cancellationToken = default)
        {
            await using var command = context.CreateCommand(sql, true, parameters, data);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task<T> ReadForUpdateAsync<T>(string sql, object parameters, CancellationToken cancellationToken = default)
        {
            await using var command = context.CreateCommand(sql, true, parameters);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new KeyNotFoundException("The required database resource was not found.");
            }

            var result = SqlExecutor.MapRow<T>(reader);
            if (await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The locked query returned multiple rows.");
            }

            return result;
        }
    }
}
