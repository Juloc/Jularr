namespace Jularr.Backend.Data.Common
{
    public interface IServiceOutput
    {
    }

    public readonly record struct NoData;

    public sealed class UpdateResult : BaseResponse
    {
        public UpdateResult(bool success)
        {
            Success = success;
        }

        public bool Success { get; }
    }
}
