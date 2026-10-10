using System.Data;
using Jularr.Infrastructure.Sql;
using Npgsql;

namespace Jularr.Service.Core;

public interface IServiceGate
{
    Task RequireModulesAsync(ModuleRequirement modules, CancellationToken cancellationToken);
    Task<ServiceCaller> RequireCallerAsync(ServiceArea area, CancellationToken cancellationToken);
    Task RequireAccessAsync(ServiceCaller caller, ServicePermission permission, IReadOnlyList<ResourceTarget> targets, CancellationToken cancellationToken);
    Task RequireTransactionalAccessAsync(ServiceCaller caller, ServicePermission permission, IReadOnlyList<ResourceTarget> targets, LogicContext context, CancellationToken cancellationToken);
}

public sealed class ServiceRuntime(NpgsqlDataSource database, IServiceGate gate)
{
    public IServiceGate Gate { get; } = gate ?? throw new ArgumentNullException(nameof(gate));

    public SqlContext CreateSqlContext() => new(database);

    public void ValidateAll(IEnumerable<IServiceDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        foreach (var definition in definitions)
        {
            definition.ValidateDefinition();
        }
    }
}

public sealed class ServiceContext
{
    public ServiceCaller Access { get; }
    public SqlContext.SqlReadCommands ReadSql { get; }
    private readonly LogicContext? _logic;

    public LogicContext Logic => _logic ?? throw new InvalidOperationException("Read services cannot access mutating Logic commands.");

    internal ServiceContext(ServiceCaller caller, SqlContext sql, bool canMutate)
    {
        Access = caller;
        sql.SetAuthorizedScope(caller.AccountId, caller.ProfileId);
        ReadSql = sql.ReadSql;
        _logic = canMutate ? new LogicContext(sql) : null;
    }
}
