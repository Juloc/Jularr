using Jularr.Backend.Data.Enums.Sql;
using System.Data;
using Jularr.Backend.Data;
using Npgsql;

namespace Jularr.Backend.Infrastructure.Sql
{
    internal sealed class SqlContext : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _database;
        private readonly SqlDatabase? _lane;
        private readonly SqlLogicCommands _logicSql;
        private NpgsqlConnection? _connection;
        private NpgsqlTransaction? _transaction;
        private SqlAccessMode? _mode;
        private IsolationLevel? _pendingReadIsolation;
        private bool _finished;
        private bool _jitDisabled;
        private TimeSpan? _statementTimeout;
        private int _shortBatchActive;
        private bool _readEnded;
        private bool _disposed;

        public SqlReadCommands ReadSql { get; }

        // A connection is held with its transaction open: work outside the database must not wait now.
        internal bool InTransaction => _transaction is not null && !_finished;

        // A short batch runs; its transaction belongs to the batch's own call path (TransactionMarker).
        internal bool InShortBatch => Volatile.Read(ref _shortBatchActive) != 0;

        public SqlContext(NpgsqlDataSource database)
        {
            _database = database;
            ReadSql = new SqlReadCommands(this);
            _logicSql = new SqlLogicCommands(this);
        }

        // A lane counts the connections it lends.
        public SqlContext(SqlDatabase database) : this(database.DataSource)
        {
            _lane = database;
        }

        public async Task BeginAsync(SqlAccessMode mode, IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            if (_transaction is not null || _connection is not null || _finished)
            {
                throw new InvalidOperationException("A SQL transaction has already been started or completed.");
            }

            var connection = _lane is null ? await _database.OpenConnectionAsync(cancellationToken) : await _lane.OpenAsync(cancellationToken);

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
                    _jitDisabled = false;
                    _statementTimeout = null;
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
                _lane?.Release();
                throw;
            }
        }

        internal void PrepareReadTransaction(IsolationLevel isolationLevel)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_mode is not null || _connection is not null || _transaction is not null || _finished)
            {
                throw new InvalidOperationException("A SQL transaction has already been configured.");
            }

            // Cache hits avoid connections.
            _mode = SqlAccessMode.ReadOnly;
            _pendingReadIsolation = isolationLevel;
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
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_pendingReadIsolation is not null)
            {
                _pendingReadIsolation = null;
                _finished = true;
                return;
            }

            if (_readEnded)
            {
                return;
            }

            RequireActive();
            await _transaction!.CommitAsync(cancellationToken);
            _finished = true;
        }

        // A read service that has read everything ends its transaction early and gives the connection back before slow work; no SQL may follow.
        internal async Task EndReadAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_mode != SqlAccessMode.ReadOnly || _finished || Volatile.Read(ref _shortBatchActive) != 0)
            {
                throw new InvalidOperationException("Only an open read transaction can end early.");
            }

            if (_transaction is not null)
            {
                await _transaction.CommitAsync(cancellationToken);
            }

            await CloseTransactionAsync();
            _finished = true;
            _readEnded = true;
        }

        internal async Task<TResult> RunShortBatchAsync<TResult>(
            SqlAccessMode mode,
            Func<CancellationToken, Task<TResult>> action,
            Func<CancellationToken, Task> transactionalAccessCheck,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(action);
            ArgumentNullException.ThrowIfNull(transactionalAccessCheck);
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Reject concurrent transaction batches.
            if (Interlocked.CompareExchange(ref _shortBatchActive, 1, 0) != 0)
            {
                throw new InvalidOperationException("Concurrent SQL batches on one context are forbidden.");
            }

            try
            {
                if (_connection is not null || _transaction is not null || _mode is not null || _finished)
                {
                    throw new InvalidOperationException("An Execute batch cannot start inside another transaction.");
                }

                TransactionMarker.EnterBatch(this);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));

                await BeginAsync(mode, IsolationLevel.ReadCommitted, timeout.Token);

                try
                {
                    await transactionalAccessCheck(timeout.Token);
                    var result = await action(timeout.Token);
                    await CommitAsync(timeout.Token);
                    return result;
                }
                finally
                {
                    // Dispose; unfinished work rolls back.
                    await CloseTransactionAsync();
                    _finished = false;
                }
            }
            finally
            {
                Volatile.Write(ref _shortBatchActive, 0);
            }
        }

        private async ValueTask CloseTransactionAsync()
        {
            var transaction = _transaction;
            var connection = _connection;
            _transaction = null;
            _connection = null;
            _mode = null;
            _pendingReadIsolation = null;

            try
            {
                if (transaction is not null)
                {
                    await transaction.DisposeAsync();
                }
            }
            finally
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                    _lane?.Release();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            await CloseTransactionAsync();
        }

        private void RequireActive()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_transaction is null || _finished)
            {
                throw new InvalidOperationException("A live SQL transaction is required.");
            }
        }

        // Both facades share binding and execution.
        private async Task<NpgsqlCommand> CreateCommandAsync(
            SqlStatement sql,
            bool allowMutation,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(sql);
            ArgumentException.ThrowIfNullOrWhiteSpace(sql.Statement);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_readEnded)
            {
                throw new InvalidOperationException("The read transaction has ended; no SQL may follow EndReadAsync.");
            }

            // Open lazy read transactions.
            if (_pendingReadIsolation is { } isolation)
            {
                _pendingReadIsolation = null;
                await BeginAsync(SqlAccessMode.ReadOnly, isolation, cancellationToken);
            }

            RequireActive();
            if (allowMutation && _mode != SqlAccessMode.ReadWrite)
            {
                throw new InvalidOperationException("Mutation commands require a read-write transaction.");
            }

            if (!allowMutation)
            {
                SqlReadGuard.RequireReadStatement(sql.Statement);
            }

            // Short interactive reads avoid compilation; the setting ends with this transaction.
            if (sql.DisableJit && !_jitDisabled)
            {
                await using var configure = new NpgsqlCommand("SET LOCAL jit = off", _connection!, _transaction);
                await configure.ExecuteNonQueryAsync(cancellationToken);
                _jitDisabled = true;
            }

            // A statement's own limit holds for this statement; Npgsql waits a little longer than the server.
            if (sql.StatementTimeout is { } requested)
            {
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requested, TimeSpan.Zero, nameof(sql));
            }

            if (sql.StatementTimeout != _statementTimeout)
            {
                await using var limit = sql.StatementTimeout is { } own
                    ? new NpgsqlCommand("SELECT set_config('statement_timeout', @Timeout, TRUE)", _connection!, _transaction) { Parameters = { new("Timeout", SqlDatabase.Milliseconds(own)) } }
                    : new NpgsqlCommand("SET LOCAL statement_timeout TO DEFAULT", _connection!, _transaction);
                await limit.ExecuteNonQueryAsync(cancellationToken);
                _statementTimeout = sql.StatementTimeout;
            }

            var command = new NpgsqlCommand(sql.Statement, _connection!, _transaction);
            try
            {
                if (sql.StatementTimeout is { } timeout)
                {
                    command.CommandTimeout = SqlDatabase.CommandTimeout(timeout);
                }

                command.Parameters.AddRange(SqlExecutor.Bind(sql));
                return command;
            }
            catch
            {
                command.Dispose();
                throw;
            }
        }

        internal SqlReadCommands LogicReads => _mode == SqlAccessMode.ReadWrite ? _logicSql : ReadSql;

        public class SqlReadCommands
        {
            private readonly SqlContext _context;
            private readonly bool _allowMutation;

            internal SqlReadCommands(SqlContext context, bool allowMutation = false)
            {
                _context = context;
                _allowMutation = allowMutation;
            }

            public async Task<object?> ExecuteScalarAsync(SqlStatement sql, CancellationToken cancellationToken = default)
            {
                await using var command = await _context.CreateCommandAsync(sql, _allowMutation, cancellationToken);
                return await command.ExecuteScalarAsync(cancellationToken);
            }

            public async Task<T?> ReadOptionalAsync<T>(SqlStatement sql, CancellationToken cancellationToken = default) where T : class
            {
                await using var command = await _context.CreateCommandAsync(sql, _allowMutation, cancellationToken);
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken);

                // Require zero or one row.
                if (!await reader.ReadAsync(cancellationToken))
                {
                    return null;
                }

                var result = SqlExecutor.MapRow<T>(reader);
                if (await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException("A single-row query returned multiple results.");
                }

                return result;
            }

            public async Task<T> ReadRequiredAsync<T>(SqlStatement sql, CancellationToken cancellationToken = default) where T : class
            {
                return await ReadOptionalAsync<T>(sql, cancellationToken)
                    ?? throw new KeyNotFoundException("The required database resource was not found.");
            }

            public async Task<IReadOnlyList<T>> ReadListAsync<T>(SqlStatement sql, CancellationToken cancellationToken = default)
            {
                await using var command = await _context.CreateCommandAsync(sql, _allowMutation, cancellationToken);
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken);
                var items = new List<T>();

                while (await reader.ReadAsync(cancellationToken))
                {
                    items.Add(SqlExecutor.MapRow<T>(reader));
                }

                return items;
            }

            public async Task<PageResult<T>> ReadPageAsync<T>(SqlStatement sql, CancellationToken cancellationToken = default)
            {
                var request = sql.Page ?? throw new InvalidOperationException("Paged SQL requires an explicit PageRequest.");
                await using var command = await _context.CreateCommandAsync(sql, _allowMutation, cancellationToken);
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken);
                var items = new List<T>();

                // Reject an unbounded page.
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (items.Count >= request.PageSize)
                    {
                        throw new InvalidOperationException("The paged SQL query exceeded its declared PageSize.");
                    }

                    items.Add(SqlExecutor.MapRow<T>(reader));
                }

                return new PageResult<T>(items, request);
            }
        }

        public sealed class SqlLogicCommands : SqlReadCommands
        {
            private readonly SqlContext _context;

            internal SqlLogicCommands(SqlContext context) : base(context, allowMutation: true)
            {
                _context = context;
            }

            public async Task<int> ExecuteAsync(SqlStatement sql, CancellationToken cancellationToken = default)
            {
                await using var command = await _context.CreateCommandAsync(sql, allowMutation: true, cancellationToken);
                return await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }
}
