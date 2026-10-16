using System.Text.RegularExpressions;
using Jularr.Integrations.Data.Media;

namespace Jularr.Integrations.Logic.Media
{
    // Reads the tags at the start and end of an audio file; an unreadable or untagged file has none.
    internal static partial class AudioTagReader
    {
        public static AudioTags? Read(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                return Read(Path.GetFileName(path), stream);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // The name's extension tells TagLib the format; the stream must seek.
        public static AudioTags? Read(string name, Stream stream)
        {
            try
            {
                using var file = TagLib.File.Create(new StreamFile(name, stream), TagLib.ReadStyle.None);
                var tag = file.Tag;
                var title = tag.Title?.Trim() is { Length: > 0 and <= 500 } value ? value : null;
                var releaseGroup = tag.MusicBrainzReleaseGroupId?.Trim().ToLowerInvariant() is { } id && Mbid().IsMatch(id) ? id : null;
                return title is null && tag.Track == 0 && releaseGroup is null ? null
                    : new AudioTags(title, tag.Disc is > 0 and < 100 ? (int)tag.Disc : null, tag.Track is > 0 and < 1000 ? (int)tag.Track : null, releaseGroup);
            }
            catch (Exception exception) when (exception is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private sealed class StreamFile(string name, Stream stream) : TagLib.File.IFileAbstraction
        {
            public string Name => name;
            public Stream ReadStream => stream;
            public Stream WriteStream => throw new NotSupportedException();

            // The caller owns the stream.
            public void CloseStream(Stream closed)
            {
            }
        }

        [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
        private static partial Regex Mbid();
    }
}
