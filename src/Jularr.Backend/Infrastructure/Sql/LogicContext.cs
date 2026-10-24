using Jularr.Backend.Data.Enums.Sql;

namespace Jularr.Backend.Infrastructure.Sql
{
    internal sealed class LogicContext
    {
        private readonly SqlContext _sql;
        private Func<LogicContext, SqlAccessMode, CancellationToken, Task>? _transactionalAccessCheck;

        internal LogicContext(SqlContext sql)
        {
            _sql = sql;
        }

        public SqlContext.SqlLogicCommands Sql => _sql.RequireLogicSql();
        public SqlContext.SqlReadCommands ReadSql => _sql.ReadSql;

        public Task<T> ReadRequiredAsync<T>(SqlStatement sql, CancellationToken cancellationToken = default) where T : class =>
            _sql.LogicReads.ReadRequiredAsync<T>(sql, cancellationToken);

        public Task<T?> ReadOptionalAsync<T>(SqlStatement sql, CancellationToken cancellationToken = default) where T : class =>
            _sql.LogicReads.ReadOptionalAsync<T>(sql, cancellationToken);

        public Task<int> ExecuteAsyncForAffectedRows(SqlStatement sql, CancellationToken cancellationToken = default) =>
            _sql.RequireLogicSql().ExecuteAsync(sql, cancellationToken);

        internal void SetTransactionalAccessCheck(Func<LogicContext, SqlAccessMode, CancellationToken, Task> check)
        {
            ArgumentNullException.ThrowIfNull(check);

            if (_transactionalAccessCheck is not null)
            {
                throw new InvalidOperationException("The verified transactional gate cannot be replaced.");
            }

            _transactionalAccessCheck = check;
        }

        // Each batch rechecks access.
        public Task<TResult> ExecuteBatchAsync<TResult>(Func<LogicContext, CancellationToken, Task<TResult>> action, CancellationToken cancellationToken = default, SqlAccessMode mode = SqlAccessMode.ReadWrite)
        {
            ArgumentNullException.ThrowIfNull(action);

            if (_transactionalAccessCheck is null)
            {
                throw new InvalidOperationException("Short Execute batches require a centrally verified transactional gate.");
            }

            return _sql.RunShortBatchAsync(
                mode,
                token => action(this, token),
                token => _transactionalAccessCheck(this, mode, token),
                cancellationToken);
        }
    }
}
