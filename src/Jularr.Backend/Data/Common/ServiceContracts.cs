namespace Jularr.Backend.Data.Common
{
    public interface IServiceOutput
    {
    }

    // Authorized file responses with HTTP range support.
    public interface IFileOutput
    {
        string FilePath { get; }
        string ContentType { get; }
    }

    // A required runtime resource is temporarily unavailable.
    public sealed class ServiceUnavailableException(string code) : Exception("The requested operation is temporarily unavailable.")
    {
        public string Code { get; } = code;
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
