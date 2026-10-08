using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Jularr.Web.Data;

public static class DatabaseTransactions
{
    // Runs the work as one transaction, or inside the one the caller already holds. An exception rolls it back, and so does a work that returns false.
    public static async Task<bool> InTransactionAsync(this DatabaseFacade database, Func<Task<bool>> work, CancellationToken cancellationToken)
    {
        if (database.CurrentTransaction is not null)
        {
            return await work();
        }

        return await database.CreateExecutionStrategy().ExecuteAsync(
            async () =>
            {
                await using var transaction = await database.BeginTransactionAsync(cancellationToken);
                if (!await work())
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }

                await transaction.CommitAsync(cancellationToken);
                return true;
            });
    }

    public static Task InTransactionAsync(this DatabaseFacade database, Func<Task> work, CancellationToken cancellationToken) =>
        database.InTransactionAsync(async () => { await work(); return true; }, cancellationToken);
}
