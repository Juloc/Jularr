using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Infrastructure.Sql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Jularr.Backend.Service.Core
{
    internal interface IServiceGate
    {
        Task RequireModulesAsync(ModuleRequirement modules, LogicContext context, bool canMutate, CancellationToken cancellationToken);
        Task<ServiceCaller> RequireCallerAsync(ServiceArea area, CancellationToken cancellationToken);
        Task RequireAccessAsync(ServiceCaller caller, ServicePermission permission, CancellationToken cancellationToken);
        Task RequireTransactionalAccessAsync(ServiceCaller caller, ServicePermission permission, LogicContext context, CancellationToken cancellationToken, SqlAccessMode mode = SqlAccessMode.ReadWrite);
    }

    internal sealed class ServiceRuntime(SqlDatabase database, IServiceGate gate, ServiceTimings timings, ILogger? logger = null)
    {
        private readonly ILogger _logger = logger ?? NullLogger.Instance;

        public IServiceGate Gate { get; } = gate ?? throw new ArgumentNullException(nameof(gate));

        public ServiceTimings Timings { get; } = timings;

        public SqlContext CreateSqlContext() => new(database);

        // A statement the server cancelled, a lock or a connection it could not get in time, or a transaction the server ended
        // because it sat idle: the log names the service and the lane.
        public void ReportDatabaseLimit(string service, Exception exception)
        {
            var reason = exception switch
            {
                PostgresException { SqlState: PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.LockNotAvailable or IdleInTransactionTimeout } postgres => postgres.SqlState,
                NpgsqlException { InnerException: TimeoutException } => "timeout",
                _ => null
            };
            if (reason is not null)
            {
                _logger.LogWarning(exception, "{Service} reached a database limit ({Reason}) on {Application}.", service, reason, database.ApplicationName);
            }
        }

        private const string IdleInTransactionTimeout = "25P03";

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
