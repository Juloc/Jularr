namespace Jularr.Integrations.Data.Djvu
{
    // A DjVu page as djvused names it: its number (from 1) and its size in pixels at full resolution.
    internal sealed class DjvuPageSize
    {
        public DjvuPageSize(int number, int width, int height)
        {
            Number = number;
            Width = width;
            Height = height;
        }

        public int Number { get; }
        public int Width { get; }
        public int Height { get; }
    }

    // A page drawn by ddjvu as a web picture: JPEG for colour and grey, PNG for black and white.
    internal sealed class DjvuPageImage
    {
        public DjvuPageImage(byte[] bytes, string contentType, int width, int height)
        {
            Bytes = bytes;
            ContentType = contentType;
            Width = width;
            Height = height;
        }

        public byte[] Bytes { get; }
        public string ContentType { get; }
        public int Width { get; }
        public int Height { get; }
    }

    // ddjvu or djvused is not configured or does not start.
    internal sealed class DjvuUnavailableException() : Exception("DjVuLibre is not available.");
}
