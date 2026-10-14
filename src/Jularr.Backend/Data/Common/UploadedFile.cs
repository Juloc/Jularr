namespace Jularr.Backend.Data.Common
{
    // A file sent with a form; the contract only opens it, the service decides where it goes.
    public sealed class UploadedFile(string fileName, long length, Func<Stream> openRead)
    {
        public string FileName { get; } = fileName;
        public long Length { get; } = length;

        public Stream OpenRead() => openRead();
    }
}
