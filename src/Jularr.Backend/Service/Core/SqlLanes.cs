using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Infrastructure.Sql;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Core
{
    // Services of a web request use the web lane's pool, background work the worker lane's; a scope may name its lane itself.
    public static class SqlLanes
    {
        internal const int WebPoolSize = 50;
        internal const int WorkerPoolSize = 30;

        // How long the server lets a transaction sit idle before it ends the session: a last resort, not a budget.
        internal static readonly TimeSpan WebIdleInTransaction = TimeSpan.FromMinutes(10);
        internal static readonly TimeSpan WorkerIdleInTransaction = TimeSpan.FromMinutes(30);

        // Every statement's server limit unless it names its own (Database:WebStatementTimeout, Database:WorkerStatementTimeout).
        internal static readonly TimeSpan WebStatementTimeout = TimeSpan.FromSeconds(15);
        internal static readonly TimeSpan WorkerStatementTimeout = TimeSpan.FromSeconds(60);

        // Background work that serves the web, such as the live streams' look at the database.
        public static AsyncServiceScope CreateAsyncScope(this IServiceScopeFactory scopes, SqlLane lane)
        {
            var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<SqlLaneChoice>().Lane = lane;
            return scope;
        }

        internal static SqlDatabase Database(IServiceProvider provider)
        {
            var lane = provider.GetRequiredService<SqlLaneChoice>().Lane
                ?? (provider.GetRequiredService<IHttpContextAccessor>().HttpContext is null ? SqlLane.Worker : SqlLane.Web);
            return lane == SqlLane.Web ? provider.GetRequiredService<SqlDatabase>() : provider.GetRequiredKeyedService<SqlDatabase>(SqlLane.Worker);
        }
    }

    // The lane a scope chose before its first service.
    internal sealed class SqlLaneChoice
    {
        public SqlLane? Lane { get; set; }
    }

    // The most connections each lane had in use, every five minutes: the measure for the pool sizes.
    internal sealed class SqlLaneMonitor(SqlDatabase web, [FromKeyedServices(SqlLane.Worker)] SqlDatabase worker, TimeProvider time, ILogger<SqlLaneMonitor> logger) : BackgroundService
    {
        internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(Interval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                logger.LogInformation("Database connections in use at most in the last {Minutes} minutes: {WebName} {Web} of {WebPool}, {WorkerName} {Worker} of {WorkerPool}.",
                    Interval.TotalMinutes, web.ApplicationName, web.TakePeak(), web.PoolSize, worker.ApplicationName, worker.TakePeak(), worker.PoolSize);
            }
        }
    }
}
