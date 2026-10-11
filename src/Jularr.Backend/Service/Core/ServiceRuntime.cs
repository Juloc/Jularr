using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Infrastructure.Sql;
using Npgsql;

namespace Jularr.Backend.Service.Core
{
    internal interface IServiceGate
    {
        Task RequireModulesAsync(ModuleRequirement modules, CancellationToken cancellationToken);
        Task<ServiceCaller> RequireCallerAsync(ServiceArea area, CancellationToken cancellationToken);
        Task RequireAccessAsync(ServiceCaller caller, ServicePermission permission, CancellationToken cancellationToken);
        Task RequireTransactionalAccessAsync(ServiceCaller caller, ServicePermission permission, LogicContext context, CancellationToken cancellationToken);
    }

    internal sealed class ServiceRuntime(NpgsqlDataSource database, IServiceGate gate)
    {
        public IServiceGate Gate { get; } = gate ?? throw new ArgumentNullException(nameof(gate));

        public SqlContext CreateSqlContext() => new(database);

        public TService Create<TService>() where TService : Service, new()
        {
            return Create(new TService());
        }

        public TService Create<TService>(TService service) where TService : Service
        {
            ArgumentNullException.ThrowIfNull(service);
            service.BindRuntime(this);
            return service;
        }

        public void ValidateAll(IEnumerable<IServiceDefinition> definitions)
        {
            ArgumentNullException.ThrowIfNull(definitions);

            foreach (var definition in definitions)
            {
                definition.ValidateDefinition();
            }
        }
    }
}
