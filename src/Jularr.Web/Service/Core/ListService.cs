namespace Jularr.Service.Core;

public sealed record ServiceSortKey<TSort>
    where TSort : struct, Enum
{
    public TSort Value { get; }
    public bool IsDefault { get; }

    private ServiceSortKey(TSort value, bool isDefault)
    {
        Value = value;
        IsDefault = isDefault;
    }

    public static ServiceSortKey<TSort> Default(TSort value) => new(value, true);

    public static ServiceSortKey<TSort> Additional(TSort value) => new(value, false);
}

public abstract class UserListService<TParameters, TSort>(ServiceRuntime runtime) : UserReadService<TParameters>(runtime)
    where TSort : struct, Enum
{
    private IReadOnlyList<ServiceSortKey<TSort>>? _sortKeys;

    protected abstract IReadOnlyList<ServiceSortKey<TSort>> GetSortKeys();
    protected abstract TSort? GetRequestedSort(TParameters parameters);

    public override void ValidateDefinition()
    {
        base.ValidateDefinition();
        ValidateSortKeys();
    }

    protected TSort ResolveSortKey(TParameters parameters)
    {
        var sorts = ValidateSortKeys();
        var requested = GetRequestedSort(parameters) ?? sorts.Single(sort => sort.IsDefault).Value;

        if (!sorts.Any(sort => EqualityComparer<TSort>.Default.Equals(sort.Value, requested)))
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "The requested sort key is not allowed.");
        }

        return requested;
    }

    private IReadOnlyList<ServiceSortKey<TSort>> ValidateSortKeys()
    {
        if (_sortKeys is not null)
        {
            return _sortKeys;
        }

        var keys = GetSortKeys()?.ToArray() ?? throw new InvalidOperationException("List services require sort keys.");
        if (keys.Length == 0 || keys.Count(key => key.IsDefault) != 1
            || keys.Any(key => !Enum.IsDefined(key.Value))
            || keys.Select(key => key.Value).Distinct().Count() != keys.Length)
        {
            throw new InvalidOperationException("Sort keys must be valid and unique, with exactly one default.");
        }

        _sortKeys = Array.AsReadOnly(keys);
        return _sortKeys;
    }
}

public abstract class AdminListService<TParameters, TSort>(ServiceRuntime runtime) : AdminReadService<TParameters>(runtime)
    where TSort : struct, Enum
{
    private IReadOnlyList<ServiceSortKey<TSort>>? _sortKeys;

    protected abstract IReadOnlyList<ServiceSortKey<TSort>> GetSortKeys();
    protected abstract TSort? GetRequestedSort(TParameters parameters);

    public override void ValidateDefinition()
    {
        base.ValidateDefinition();
        ValidateSortKeys();
    }

    protected TSort ResolveSortKey(TParameters parameters)
    {
        var sorts = ValidateSortKeys();
        var requested = GetRequestedSort(parameters) ?? sorts.Single(sort => sort.IsDefault).Value;

        if (!sorts.Any(sort => EqualityComparer<TSort>.Default.Equals(sort.Value, requested)))
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "The requested sort key is not allowed.");
        }

        return requested;
    }

    private IReadOnlyList<ServiceSortKey<TSort>> ValidateSortKeys()
    {
        if (_sortKeys is not null)
        {
            return _sortKeys;
        }

        var keys = GetSortKeys()?.ToArray() ?? throw new InvalidOperationException("List services require sort keys.");
        if (keys.Length == 0 || keys.Count(key => key.IsDefault) != 1
            || keys.Any(key => !Enum.IsDefined(key.Value))
            || keys.Select(key => key.Value).Distinct().Count() != keys.Length)
        {
            throw new InvalidOperationException("Sort keys must be valid and unique, with exactly one default.");
        }

        _sortKeys = Array.AsReadOnly(keys);
        return _sortKeys;
    }
}
