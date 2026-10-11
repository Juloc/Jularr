using System.Data;
using Jularr.Data.Common;
using Jularr.Data.Enums.Service;
using Jularr.Data.Enums.Sql;
using Jularr.Infrastructure.Sql;

namespace Jularr.Service.Core;

public abstract class Service : IServiceDefinition
{
    private ServiceRuntime? _runtime;

    protected ServiceRuntime Runtime => _runtime ?? throw new InvalidOperationException("The service runtime has not been bound.");

    internal void BindRuntime(ServiceRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (Interlocked.CompareExchange(ref _runtime, runtime, null) is not null)
        {
            throw new InvalidOperationException("A service runtime can only be bound once.");
        }
    }

    public abstract void ValidateDefinition();
    public abstract ServiceOperationType OperationType { get; }
    public abstract Type DefaultResultType { get; }
    public abstract ServiceArea ServiceArea { get; }
}

public abstract class Service<TParameters, TData> : Service
{
    private readonly Lazy<ServiceDefinition> _definition;
    private readonly AsyncLocal<Invocation?> _invocation = new();

    private Invocation Current => _invocation.Value ?? throw new InvalidOperationException("Service state is only available during an authorized invocation.");
    protected TParameters Parameters => Current.Parameters;
    protected TData Data => Current.Data;
    protected LogicContext Context => Current.Context;
    protected ServiceCaller Auth => Current.Caller;
    protected Type ResultType => Current.ResultType;
    protected CancellationToken CancellationToken => Current.CancellationToken;

    protected Service()
    {
        _definition = new Lazy<ServiceDefinition>(BuildDefinition);
    }

    protected abstract ServiceArea Area { get; }
    protected abstract ServiceOperationType GetOperationType();
    protected abstract IReadOnlyList<ServiceResultType> GetResultTypes();
    protected abstract ModuleRequirement GetInstanceModules(TParameters parameters);
    protected abstract ServicePermission GetPermission(TParameters parameters, Type resultType);
    protected virtual void ValidateParameters(TParameters parameters)
    {
    }

    protected virtual IsolationLevel? GetTransaction(TParameters parameters) => null;
    protected virtual Task ValidatePermissions(TParameters parameters) => Task.CompletedTask;
    protected virtual Task<IServiceOutput> OnRead() => throw new InvalidOperationException("The Read hook is not implemented.");
    protected virtual Task<IServiceOutput> OnCreate() => throw new InvalidOperationException("The Create hook is not implemented.");
    protected virtual Task<IServiceOutput> OnUpdate() => throw new InvalidOperationException("The Update hook is not implemented.");
    protected virtual Task<IServiceOutput> OnDelete() => throw new InvalidOperationException("The Delete hook is not implemented.");
    protected virtual Task<IServiceOutput> OnExecute() => throw new InvalidOperationException("The Execute hook is not implemented.");

    public override void ValidateDefinition() => _ = _definition.Value;
    public override ServiceOperationType OperationType => _definition.Value.OperationType;
    public override Type DefaultResultType => _definition.Value.DefaultResultType;
    public override ServiceArea ServiceArea => Area;

    public Task<IServiceOutput> ExecuteAsync(TParameters parameters, TData data, CancellationToken cancellationToken = default)
    {
        ValidateDefinition();
        var definition = _definition.Value;
        return RunAsync(parameters, data, definition.DefaultResultType, definition, cancellationToken);
    }

    public async Task<TOutput> ExecuteAsync<TOutput>(TParameters parameters, TData data, CancellationToken cancellationToken = default) where TOutput : IServiceOutput
    {
        ValidateDefinition();
        var definition = _definition.Value;
        return (TOutput)await RunAsync(parameters, data, typeof(TOutput), definition, cancellationToken);
    }

    private async Task<IServiceOutput> RunAsync(TParameters parameters, TData data, Type resultType, ServiceDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(data);

        var modules = GetInstanceModules(parameters) ?? throw new InvalidOperationException("The module requirement cannot be null.");
        await Runtime.Gate.RequireModulesAsync(modules, cancellationToken);
        if (parameters is BaseData parameterData)
        {
            parameterData.Validate();
        }

        if (data is BaseData inputData)
        {
            inputData.Validate();
        }

        ValidateParameters(parameters);

        if (!definition.ResultTypes.Contains(resultType))
        {
            throw new InvalidOperationException("The requested result type is not declared by this service.");
        }

        var caller = await Runtime.Gate.RequireCallerAsync(Area, cancellationToken);
        if (caller is null || caller.Area != Area || string.IsNullOrWhiteSpace(caller.PrincipalKey))
        {
            throw new UnauthorizedAccessException("The service caller has not been verified for this area.");
        }

        var permission = GetPermission(parameters, resultType) ?? throw new InvalidOperationException("A service permission must be declared.");
        await Runtime.Gate.RequireAccessAsync(caller, permission, cancellationToken);

        await using var sql = Runtime.CreateSqlContext();
        var isolation = GetTransaction(parameters);
        var needsTransaction = definition.OperationType != ServiceOperationType.Execute || isolation.HasValue;

        if (needsTransaction)
        {
            var mode = definition.OperationType == ServiceOperationType.Read ? SqlAccessMode.ReadOnly : SqlAccessMode.ReadWrite;
            await sql.BeginAsync(mode, isolation ?? IsolationLevel.ReadCommitted, cancellationToken);
        }

        var canMutate = definition.OperationType != ServiceOperationType.Read;
        sql.SetAuthorizedScope(caller.AccountId, caller.ProfileId);
        var context = new LogicContext(sql);

        if (canMutate && needsTransaction)
        {
            await Runtime.Gate.RequireTransactionalAccessAsync(caller, permission, context, cancellationToken);
        }
        else if (definition.OperationType == ServiceOperationType.Execute)
        {
            // Execute batches must recheck access inside each short transaction.
            context.SetTransactionalAccessCheck(
                (logic, token) => Runtime.Gate.RequireTransactionalAccessAsync(caller, permission, logic, token));
        }

        var previous = _invocation.Value;
        _invocation.Value = new Invocation(parameters, data, resultType, caller, context, cancellationToken);
        try
        {
            await ValidatePermissions(parameters);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await (definition.OperationType switch
            {
                ServiceOperationType.Read => OnRead(),
                ServiceOperationType.Create => OnCreate(),
                ServiceOperationType.Update => OnUpdate(),
                ServiceOperationType.Delete => OnDelete(),
                ServiceOperationType.Execute => OnExecute(),
                _ => throw new InvalidOperationException("The service operation is unavailable.")
            });

            if (result is null || result.GetType() != resultType)
            {
                throw new InvalidOperationException("The service returned an undeclared or incorrect result type.");
            }

            if (result is BaseData output)
            {
                try
                {
                    output.Validate();
                }
                catch (DataValidationException exception)
                {
                    throw new InvalidOperationException("The service returned invalid data.", exception);
                }
            }

            if (needsTransaction)
            {
                await sql.CommitAsync(cancellationToken);
            }

            return result;
        }
        finally
        {
            _invocation.Value = previous;
        }
    }

    private ServiceDefinition BuildDefinition()
    {
        var operationType = GetOperationType();
        if (!Enum.IsDefined(operationType))
        {
            throw new InvalidOperationException("A service must declare a valid operation type.");
        }

        var declaredTypes = GetResultTypes()?.ToArray() ?? throw new InvalidOperationException("A service must declare result types.");
        if (declaredTypes.Length == 0 || declaredTypes.Count(result => result.IsDefault) != 1
            || declaredTypes.Any(result => !typeof(IServiceOutput).IsAssignableFrom(result.Type) || result.Type.IsAbstract || result.Type.IsInterface)
            || declaredTypes.Select(result => result.Type).Distinct().Count() != declaredTypes.Length)
        {
            throw new InvalidOperationException("Result types must be concrete and unique, with exactly one default.");
        }

        return new ServiceDefinition(operationType, declaredTypes.Select(result => result.Type).ToHashSet(), declaredTypes.Single(result => result.IsDefault).Type);
    }

    private sealed record ServiceDefinition(ServiceOperationType OperationType, HashSet<Type> ResultTypes, Type DefaultResultType);
    private sealed record Invocation(TParameters Parameters, TData Data, Type ResultType, ServiceCaller Caller, LogicContext Context, CancellationToken CancellationToken);
}

public abstract class UserService<TParameters, TData> : Service<TParameters, TData>
{
    protected sealed override ServiceArea Area => ServiceArea.User;
}

public abstract class AdminService<TParameters, TData> : Service<TParameters, TData>
{
    protected sealed override ServiceArea Area => ServiceArea.Admin;
}

public abstract class SystemService<TParameters, TData> : Service<TParameters, TData>
{
    protected sealed override ServiceArea Area => ServiceArea.System;
}

public abstract class PublicService<TParameters, TData> : Service<TParameters, TData>
{
    protected sealed override ServiceArea Area => ServiceArea.Public;
    protected sealed override ServicePermission GetPermission(TParameters parameters, Type resultType) => ServicePermission.Public;
}

public abstract class PublicService<TParameters> : PublicService<TParameters, NoData>
{
    protected sealed override ServiceOperationType GetOperationType() => ServiceOperationType.Read;

    public Task<IServiceOutput> ExecuteAsync(TParameters parameters, CancellationToken cancellationToken = default) => base.ExecuteAsync(parameters, default, cancellationToken);
}

public abstract class UserService<TParameters> : UserService<TParameters, NoData>
{
    protected sealed override ServiceOperationType GetOperationType() => ServiceOperationType.Read;

    public Task<IServiceOutput> ExecuteAsync(TParameters parameters, CancellationToken cancellationToken = default) => base.ExecuteAsync(parameters, default, cancellationToken);

    public Task<TOutput> ExecuteAsync<TOutput>(TParameters parameters, CancellationToken cancellationToken = default) where TOutput : IServiceOutput
        => base.ExecuteAsync<TOutput>(parameters, default, cancellationToken);
}

public abstract class UserReadService<TParameters> : UserService<TParameters>;

public abstract class AdminReadService<TParameters> : AdminService<TParameters, NoData>
{
    protected sealed override ServiceOperationType GetOperationType() => ServiceOperationType.Read;

    public Task<IServiceOutput> ExecuteAsync(TParameters parameters, CancellationToken cancellationToken = default) => base.ExecuteAsync(parameters, default, cancellationToken);

    public Task<TOutput> ExecuteAsync<TOutput>(TParameters parameters, CancellationToken cancellationToken = default) where TOutput : IServiceOutput
        => base.ExecuteAsync<TOutput>(parameters, default, cancellationToken);
}
