namespace Jularr.Backend.Infrastructure.Sql
{
    // Tells code further down a service operation's call path whether the operation, or one it runs inside, holds a SQL transaction open right now:
    // work outside the database can then refuse to wait while a connection is held.
    internal sealed class TransactionMarker
    {
        private static readonly AsyncLocal<TransactionMarker?> Current = new();
        private static readonly AsyncLocal<BatchMark?> Batch = new();

        private readonly SqlContext _sql;
        private readonly TransactionMarker? _outer;

        private TransactionMarker(SqlContext sql, string owner, TransactionMarker? outer)
        {
            _sql = sql;
            Owner = owner;
            _outer = outer;
        }

        public string Owner { get; }

        // The innermost operation on this call path whose transaction is open, or null.
        public static string? OpenTransactionOwner
        {
            get
            {
                for (var marker = Current.Value; marker is not null; marker = marker._outer)
                {
                    if (Holds(marker._sql))
                    {
                        return marker.Owner;
                    }
                }

                return null;
            }
        }

        // Marks the rest of the calling async method and everything it calls; the mark ends when that method returns.
        public static void Mark(SqlContext sql, string owner) => Current.Value = new TransactionMarker(sql, owner, Current.Value);

        // A short batch's transaction is held by the batch's own call path only, not by work running beside it on the same context (such as a periodic save
        // while the main path waits for another server). Called inside the batch's async method, so the mark ends with it.
        public static void EnterBatch(SqlContext sql) => Batch.Value = new BatchMark(sql, Batch.Value);

        private static bool Holds(SqlContext sql)
        {
            // A batch on another call path may open or close its transaction while this reads: read the batch version on both sides of the
            // transaction and read again when it changed, so a batch's transaction never counts as the caller's own.
            int version;
            bool inTransaction;
            while (true)
            {
                version = sql.ShortBatchVersion;
                inTransaction = sql.InTransaction;
                Interlocked.MemoryBarrier();
                if (sql.ShortBatchVersion == version)
                {
                    break;
                }
            }

            if (!inTransaction)
            {
                return false;
            }

            if ((version & 1) == 0)
            {
                return true;
            }

            for (var batch = Batch.Value; batch is not null; batch = batch.Outer)
            {
                if (ReferenceEquals(batch.Sql, sql))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed record BatchMark(SqlContext Sql, BatchMark? Outer);
    }
}
