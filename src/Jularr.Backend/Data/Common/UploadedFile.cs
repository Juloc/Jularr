namespace Jularr.Backend.Data.Common
{
    // A file sent with a form; the contract only opens it, the service decides where it goes.
    public sealed class UploadedFile
    {
        private readonly Func<Stream> _openRead;

        public UploadedFile(string fileName, long length, Func<Stream> openRead)
        {
            FileName = fileName;
            Length = length;
            _openRead = openRead;
        }

        public string FileName { get; }
        public long Length { get; }

        public Stream OpenRead() => _openRead();
    }
}
