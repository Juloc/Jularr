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

    // Authorized bytes from the database, such as cached book resources.
    // A file named by its content; browsers keep it for good.
    public interface IImmutableFileOutput : IFileOutput;

    public interface IBinaryOutput
    {
        byte[] Bytes { get; }
        string ContentType { get; }
    }

    // Generated text documents such as streaming playlists.
    public interface ITextOutput
    {
        string Text { get; }
        string ContentType { get; }
    }

    // A required runtime resource is temporarily unavailable.
    public sealed class ServiceUnavailableException(string code, Exception? innerException = null) : Exception("The requested operation is temporarily unavailable.", innerException)
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
