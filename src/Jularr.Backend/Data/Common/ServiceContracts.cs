namespace Jularr.Data.Common;

public interface IServiceOutput
{
}

public readonly record struct NoData;

public sealed class UpdateResult : BaseData, IServiceOutput
{
    public UpdateResult(bool success)
    {
        Success = success;
    }

    public bool Success { get; }
}
