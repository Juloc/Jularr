using System.Globalization;
using System.Security.Cryptography;
using System.Web;

namespace Jularr.Integrations.Logic.Torrent
{
    internal sealed class TorrentFile(string[] path, long length, long offset)
    {
        public string[] Path { get; } = path;
        public long Length { get; } = length;
        public long Offset { get; } = offset;
    }

    // A v1 torrent: its info hash from the exact info bytes, pieces, files with safe relative paths, and trackers.
    internal sealed class TorrentMetainfo
    {
        private const long MaximumTotal = 4L * 1024 * 1024 * 1024 * 1024;
        private const int MaximumFiles = 20_000;

        private TorrentMetainfo(byte[] rawInfo, byte[] infoHash, string name, int pieceLength, byte[] pieces, IReadOnlyList<TorrentFile> files, IReadOnlyList<Uri> trackers)
        {
            RawInfo = rawInfo;
            InfoHash = infoHash;
            Name = name;
            PieceLength = pieceLength;
            Pieces = pieces;
            Files = files;
            Trackers = trackers;
            TotalLength = files.Sum(file => file.Length);
            PieceCount = pieces.Length / 20;
        }

        public byte[] RawInfo { get; }
        public byte[] InfoHash { get; }
        public string Name { get; }
        public int PieceLength { get; }
        public byte[] Pieces { get; }
        public int PieceCount { get; }
        public IReadOnlyList<TorrentFile> Files { get; }
        public long TotalLength { get; }
        public IReadOnlyList<Uri> Trackers { get; }

        public int PieceSize(int index) => (int)Math.Min(PieceLength, TotalLength - (long)index * PieceLength);

        public ReadOnlySpan<byte> PieceHash(int index) => Pieces.AsSpan(index * 20, 20);

        public static TorrentMetainfo Parse(byte[] torrent)
        {
            if (Bencode.Decode(torrent) is not Bencode.Dictionary root || !root.Spans.TryGetValue("info", out var span))
            {
                throw new InvalidDataException("The torrent has no info dictionary.");
            }

            var trackers = new List<Uri>();
            if (root.TryGetValue("announce-list", out var tiers) && tiers is List<object> list)
            {
                foreach (var tier in list.OfType<List<object>>())
                {
                    foreach (var address in tier.OfType<byte[]>()) AddTracker(trackers, Bencode.Text(address));
                }
            }

            if (root.TryGetValue("announce", out var announce) && announce is byte[]) AddTracker(trackers, Bencode.Text(announce));
            return FromInfo(torrent.AsSpan(span), trackers);
        }

        public static TorrentMetainfo FromInfo(ReadOnlySpan<byte> rawInfo, IEnumerable<Uri> trackers)
        {
            if (Bencode.Decode(rawInfo) is not Bencode.Dictionary info) throw new InvalidDataException("The info entry is not a dictionary.");
            if (!info.TryGetValue("pieces", out var piecesValue) || piecesValue is not byte[] pieces || pieces.Length == 0 || pieces.Length % 20 != 0)
            {
                // A torrent of the second version only has no SHA-1 pieces this engine could check.
                throw new InvalidDataException("The torrent has no version 1 pieces.");
            }

            if (!info.TryGetValue("piece length", out var pieceValue) || pieceValue is not long pieceLength || pieceLength is < 16 * 1024 or > 64 * 1024 * 1024)
            {
                throw new InvalidDataException("Unsupported piece length.");
            }

            var name = SafeSegment(Bencode.Text(info.GetValueOrDefault("name.utf-8") ?? info.GetValueOrDefault("name")));
            var files = new List<TorrentFile>();
            long offset = 0;
            if (info.TryGetValue("files", out var filesValue))
            {
                if (filesValue is not List<object> entries || entries.Count is 0 or > MaximumFiles) throw new InvalidDataException("Invalid file list.");
                foreach (var entry in entries)
                {
                    if (entry is not Bencode.Dictionary file || file.GetValueOrDefault("length") is not long length || length < 0
                        || (file.GetValueOrDefault("path.utf-8") ?? file.GetValueOrDefault("path")) is not List<object> segments || segments.Count == 0)
                    {
                        throw new InvalidDataException("Invalid file entry.");
                    }

                    // Padding files of hybrid torrents hold no data of the release.
                    if (file.GetValueOrDefault("attr") is byte[] attributes && attributes.Contains((byte)'p'))
                    {
                        files.Add(new TorrentFile([], length, offset));
                    }
                    else
                    {
                        files.Add(new TorrentFile([name, .. segments.Select(segment => SafeSegment(Bencode.Text(segment)))], length, offset));
                    }

                    offset += length;
                }
            }
            else if (info.GetValueOrDefault("length") is long single && single >= 0)
            {
                files.Add(new TorrentFile([name], single, 0));
                offset = single;
            }
            else
            {
                throw new InvalidDataException("The torrent names no files.");
            }

            if (offset is <= 0 or > MaximumTotal || (offset + pieceLength - 1) / pieceLength != pieces.Length / 20)
            {
                throw new InvalidDataException("The pieces do not match the files.");
            }

            var hash = SHA1.HashData(rawInfo);
            return new TorrentMetainfo(rawInfo.ToArray(), hash, name, (int)pieceLength, pieces, files, trackers.Distinct().ToArray());
        }

        // One path segment that stays inside the job folder on every platform.
        private static string SafeSegment(string segment)
        {
            var clean = new string([.. segment.Select(character => character < 32 || "<>:\"/\\|?*".Contains(character) ? '_' : character)]).Trim().TrimEnd('.');
            if (clean.Length == 0 || clean is "." or ".." || clean.Length > 240) throw new InvalidDataException("Unsafe path in torrent.");
            return clean;
        }

        private static void AddTracker(List<Uri> trackers, string address)
        {
            if (Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "udp" && !trackers.Contains(uri)) trackers.Add(uri);
        }
    }

    // A magnet link: the v1 info hash, a display name, trackers and peers named in it.
    internal sealed class MagnetLink(byte[] infoHash, string? name, IReadOnlyList<Uri> trackers, IReadOnlyList<(string Host, int Port)> peers)
    {
        public byte[] InfoHash { get; } = infoHash;
        public string? Name { get; } = name;
        public IReadOnlyList<Uri> Trackers { get; } = trackers;
        public IReadOnlyList<(string Host, int Port)> Peers { get; } = peers;

        public static MagnetLink Parse(string link)
        {
            if (!link.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) || link.Length > 8192) throw new InvalidDataException("Not a magnet link.");
            byte[]? hash = null;
            string? name = null;
            var trackers = new List<Uri>();
            var peers = new List<(string, int)>();
            foreach (var part in link[8..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=');
                if (separator <= 0) continue;
                var key = part[..separator].ToLowerInvariant();
                var value = HttpUtility.UrlDecode(part[(separator + 1)..]);
                if (key == "xt" && value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase)) hash = Hash(value[9..]);
                else if (key == "dn") name = value;
                else if (key.StartsWith("tr", StringComparison.Ordinal) && Uri.TryCreate(value, UriKind.Absolute, out var tracker) && tracker.Scheme is "http" or "https" or "udp") trackers.Add(tracker);
                else if (key == "x.pe" && value.LastIndexOf(':') is var colon and > 0
                    && int.TryParse(value[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536)
                {
                    peers.Add((value[..colon].Trim('[', ']'), port));
                }
            }

            return new MagnetLink(hash ?? throw new InvalidDataException("The magnet link has no v1 info hash."), name, trackers, peers);
        }

        private static byte[] Hash(string text)
        {
            if (text.Length == 40) return Convert.FromHexString(text);
            if (text.Length != 32) throw new InvalidDataException("Invalid info hash.");

            // Base32 as some magnet links write it.
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var bytes = new byte[20];
            var buffer = 0;
            var bits = 0;
            var index = 0;
            foreach (var character in text.ToUpperInvariant())
            {
                var value = alphabet.IndexOf(character);
                if (value < 0) throw new InvalidDataException("Invalid info hash.");
                buffer = (buffer << 5) | value;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    bytes[index++] = (byte)(buffer >> bits);
                }
            }

            return bytes;
        }
    }
}
