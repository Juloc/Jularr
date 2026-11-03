using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.Djvu;
using SkiaSharp;

namespace Jularr.Integrations.Logic.Djvu
{
    // DjVuLibre's djvused counts and measures a DjVu book's pages, its ddjvu draws one page; no shell, bounded run time, output and picture size.
    internal sealed partial class DjvuTools
    {
        public const int MaximumPages = 5000;
        public const int MaximumWidth = 1800;
        public const int MaximumHeight = 2400;

        // A drawn page is at most the bounded box in colour, with room for the PNM header.
        private const int MaximumOutputBytes = MaximumWidth * MaximumHeight * 3 + 1024;
        private const int MaximumTextBytes = 1024 * 1024;
        private const int MaximumSide = 100_000;
        private static readonly TimeSpan MeasureTime = TimeSpan.FromSeconds(30);

        [GeneratedRegex(@"width=(\d+)\s+height=(\d+)")]
        private static partial Regex SizeLine();

        public string? DdjvuPath { get; set; }
        public string? DjvusedPath { get; set; }

        // Each page has this long to be drawn.
        public TimeSpan PageTime { get; set; } = TimeSpan.FromSeconds(60);

        public bool IsAvailable => !string.IsNullOrWhiteSpace(DdjvuPath) && !string.IsNullOrWhiteSpace(DjvusedPath);

        // The number of pages; an unreadable document throws InvalidDataException.
        public async Task<int> CountPagesAsync(string path, CancellationToken ct)
        {
            var output = await RunAsync(DjvusedPath, ["-e", "n", Checked(path)], MeasureTime, MaximumTextBytes, ct);
            return int.TryParse(Encoding.ASCII.GetString(output).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0
                ? count
                : throw new InvalidDataException("djvused names no page count.");
        }

        // The full-resolution size of the given pages (numbers from 1), in one run.
        public async Task<IReadOnlyList<DjvuPageSize>> ReadSizesAsync(string path, IReadOnlyList<int> numbers, CancellationToken ct)
        {
            if (numbers.Count is 0 or > 64 || numbers.Any(number => number is < 1 or > MaximumPages))
            {
                throw new ArgumentOutOfRangeException(nameof(numbers));
            }

            var script = string.Join("; ", numbers.Select(number => "select " + number.ToString(CultureInfo.InvariantCulture) + "; size"));
            var lines = SizeLine().Matches(Encoding.ASCII.GetString(await RunAsync(DjvusedPath, ["-e", script, Checked(path)], MeasureTime, MaximumTextBytes, ct)));
            if (lines.Count != numbers.Count)
            {
                throw new InvalidDataException("djvused did not measure every page.");
            }

            return [.. numbers.Select((number, index) => new DjvuPageSize(number,
                int.Parse(lines[index].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture), int.Parse(lines[index].Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture)))];
        }

        // One page within the bounded box, never larger than it is; an unreadable page throws InvalidDataException, a page that takes too long TimeoutException.
        public async Task<DjvuPageImage> RenderAsync(string path, DjvuPageSize page, CancellationToken ct)
        {
            if (page.Number is < 1 or > MaximumPages || page.Width is < 1 or > MaximumSide || page.Height is < 1 or > MaximumSide)
            {
                throw new InvalidDataException("The page has no usable size.");
            }

            // A page larger than the box is drawn into it at its own proportions.
            var arguments = new List<string> { "-format=pnm", "-page=" + page.Number.ToString(CultureInfo.InvariantCulture) };
            if (page.Width > MaximumWidth || page.Height > MaximumHeight)
            {
                arguments.Add("-size=" + MaximumWidth.ToString(CultureInfo.InvariantCulture) + "x" + MaximumHeight.ToString(CultureInfo.InvariantCulture));
            }

            arguments.Add(Checked(path));
            return Encode(await RunAsync(DdjvuPath, arguments, PageTime, MaximumOutputBytes, ct));
        }

        // A full local path never reads as an option (neither tool takes "--").
        private static string Checked(string path) =>
            Path.IsPathFullyQualified(path) && !path.StartsWith('-') ? path : throw new ArgumentException("The DjVu file needs a full path.", nameof(path));

        // A PBM page becomes a PNG, a grey or colour page a JPEG.
        internal static DjvuPageImage Encode(ReadOnlySpan<byte> pnm)
        {
            if (pnm.Length < 3 || pnm[0] != 'P' || pnm[1] is not ((byte)'4' or (byte)'5' or (byte)'6'))
            {
                throw new InvalidDataException("ddjvu drew no PNM picture.");
            }

            var kind = pnm[1];
            var position = 2;
            var width = Number(pnm, ref position);
            var height = Number(pnm, ref position);
            var maximum = kind == '4' ? 1 : Number(pnm, ref position);
            if (width is < 1 or > MaximumWidth || height is < 1 or > MaximumHeight || maximum != (kind == '4' ? 1 : 255) || position >= pnm.Length || !IsSpace(pnm[position]))
            {
                throw new InvalidDataException("The PNM picture has an unexpected header.");
            }

            var pixels = pnm[(position + 1)..];
            var rowBytes = kind switch { (byte)'4' => (width + 7) / 8, (byte)'5' => width, _ => width * 3 };
            if (pixels.Length < rowBytes * height)
            {
                throw new InvalidDataException("The PNM picture is cut short.");
            }

            using var bitmap = new SKBitmap(new SKImageInfo(width, height, kind == '6' ? SKColorType.Rgba8888 : SKColorType.Gray8, SKAlphaType.Opaque));
            var target = bitmap.GetPixelSpan();
            for (var y = 0; y < height; y++)
            {
                var row = pixels.Slice(y * rowBytes, rowBytes);
                var line = target.Slice(y * bitmap.RowBytes);
                for (var x = 0; x < width; x++)
                {
                    switch (kind)
                    {
                        case (byte)'4':
                            line[x] = (row[x >> 3] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
                            break;
                        case (byte)'5':
                            line[x] = row[x];
                            break;
                        default:
                            (line[x * 4], line[x * 4 + 1], line[x * 4 + 2], line[x * 4 + 3]) = (row[x * 3], row[x * 3 + 1], row[x * 3 + 2], 255);
                            break;
                    }
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            var png = kind == '4';
            using var data = image.Encode(png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 85) ?? throw new InvalidDataException("The page could not be encoded.");
            return new DjvuPageImage(data.ToArray(), png ? "image/png" : "image/jpeg", width, height);
        }

        // A decimal header field after whitespace and comments.
        private static int Number(ReadOnlySpan<byte> pnm, ref int position)
        {
            while (position < pnm.Length && (IsSpace(pnm[position]) || pnm[position] == '#'))
            {
                if (pnm[position] == '#')
                {
                    while (position < pnm.Length && pnm[position] != '\n') position++;
                }
                else
                {
                    position++;
                }
            }

            var value = 0;
            var digits = 0;
            while (position < pnm.Length && pnm[position] is >= (byte)'0' and <= (byte)'9' && digits < 7)
            {
                value = value * 10 + pnm[position] - '0';
                position++;
                digits++;
            }

            return digits == 0 ? throw new InvalidDataException("The PNM header has no number.") : value;
        }

        private static bool IsSpace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

        // No shell, standard input closed, stdout bounded and stderr drained; a tool that cannot start throws DjvuUnavailableException.
        private static async Task<byte[]> RunAsync(string? executable, IReadOnlyList<string> arguments, TimeSpan limit, int maximumBytes, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(executable))
            {
                throw new DjvuUnavailableException();
            }

            var start = new ProcessStartInfo(executable)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            Process process;
            try
            {
                process = Process.Start(start) ?? throw new DjvuUnavailableException();
            }
            catch (Win32Exception)
            {
                throw new DjvuUnavailableException();
            }

            using var owned = process;
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limit);
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            try
            {
                // Output beyond the bound means the page is not what was asked for.
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (output.Length + read > maximumBytes)
                    {
                        throw new InvalidDataException("The tool wrote more than its bound.");
                    }

                    output.Write(buffer, 0, read);
                }

                await process.WaitForExitAsync(timeout.Token);
                await errors;
                return process.ExitCode == 0 ? output.ToArray() : throw new InvalidDataException("The tool refused the document.");
            }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("The tool ran out of time.");
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await errors.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }
}
