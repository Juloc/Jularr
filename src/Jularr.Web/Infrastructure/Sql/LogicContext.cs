namespace Jularr.Infrastructure.Sql;

public sealed class LogicContext
{
    private readonly SqlContext _sql;

    internal LogicContext(SqlContext sql)
    {
        _sql = sql;
    }

    public SqlContext.SqlLogicCommands Sql => _sql.RequireLogicSql();
}
