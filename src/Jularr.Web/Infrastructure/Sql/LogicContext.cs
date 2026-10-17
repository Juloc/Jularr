namespace Jularr.Infrastructure.Sql;

public sealed class LogicContext
{
    private readonly SqlContext _sql;
    private Func<LogicContext, CancellationToken, Task>? _transactionalAccessCheck;

    internal LogicContext(SqlContext sql)
    {
        _sql = sql;
    }

    // Within a Service-owned write transaction, every Logic call shares this exact SQL context.
    public SqlContext.SqlLogicCommands Sql => _sql.RequireLogicSql();

    internal void SetTransactionalAccessCheck(Func<LogicContext, CancellationToken, Task> check)
    {
        ArgumentNullException.ThrowIfNull(check);

        if (_transactionalAccessCheck is not null)
        {
            throw new InvalidOperationException("The verified transactional gate cannot be replaced.");
        }

        _transactionalAccessCheck = check;
    }

    // Execute without an outer transaction: a Logic owner may write several short, atomic batches.
    // Each batch opens and disposes one bounded transaction and rechecks permissions inside it.
    public Task<TResult> ExecuteBatchAsync<TResult>(Func<LogicContext, CancellationToken, Task<TResult>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_transactionalAccessCheck is null)
        {
            throw new InvalidOperationException("Short Execute batches require a centrally verified transactional gate.");
        }

        return _sql.RunShortWriteBatchAsync(
            token => action(this, token),
            token => _transactionalAccessCheck(this, token),
            cancellationToken);
    }
}
