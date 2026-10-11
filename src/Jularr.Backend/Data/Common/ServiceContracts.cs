namespace Jularr.Data.Common;

public interface IServiceOutput
{
}

public readonly record struct NoData;

public sealed record UpdateResult(bool Success) : IServiceOutput;
