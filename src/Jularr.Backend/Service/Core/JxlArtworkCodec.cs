using System.Diagnostics;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Service.Core
{
    // Uses the packaged libjxl command-line encoder. Run it only behind ArtworkCache's single-encoder limit.
    // Never passes user-controlled command fragments to a shell.
    internal sealed class JxlArtworkCodec(string executable = "cjxl", string decoder = "djxl")
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(35);
        private const int MaximumOutputBytes = 4 * 1024 * 1024;
        private static readonly SemaphoreSlim NativeEncoder = new(1, 1);

        internal static string[] EncoderOptions(ArtworkQuality quality, bool progressive) =>
            [
                "--effort=4",
                "--num_threads=2",
                "--distance=" + (quality switch
                {
                    ArtworkQuality.Balanced => "2",
                    ArtworkQuality.High => "1",
                    ArtworkQuality.VisuallyLossless => "0.5",
                    ArtworkQuality.Lossless => "0",
                    _ => "1"
                }),
                ..(progressive ? new[] { "--progressive" } : Array.Empty<string>()),
                "--quiet"
            ];

        public async Task<byte[]?> EncodeAsync(byte[] png, ArtworkQuality quality, bool progressive, CancellationToken ct)
        {
            if (png.Length is < 64 or > 8 * 1024 * 1024) return null;
            await NativeEncoder.WaitAsync(ct);
            try
            {
                return await TransformAsync(executable, png, ".png", ".jxl",
                    EncoderOptions(quality, progressive), ct);
            }
            finally
            {
                NativeEncoder.Release();
            }
        }

        // Provider cover downloads have been validated and converted to JPEG. Force pixel
        // encoding instead of JPEG bitstream reconstruction so progressive mode is respected.
        internal static string[] JpegEncoderOptions(ArtworkQuality quality, bool progressive) =>
            [..EncoderOptions(quality, progressive), "--lossless_jpeg=0"];

        public async Task<byte[]?> EncodeJpegAsync(byte[] jpeg, ArtworkQuality quality, bool progressive, CancellationToken ct)
        {
            if (jpeg.Length is < 64 or > 8 * 1024 * 1024) return null;
            await NativeEncoder.WaitAsync(ct);
            try
            {
                return await TransformAsync(executable, jpeg, ".jpg", ".jxl",
                    JpegEncoderOptions(quality, progressive), ct);
            }
            finally
            {
                NativeEncoder.Release();
            }
        }

        // Decode a NAS JXL only in the worker, then process it through the same bounded image pipeline.
        public async Task<byte[]?> DecodeAsync(byte[] jxl, CancellationToken ct)
        {
            if (jxl.Length is < 32 or > 12 * 1024 * 1024) return null;
            return await TransformAsync(decoder, jxl, ".jxl", ".png", ["--quiet"], ct);
        }

        private static async Task<byte[]?> TransformAsync(string executable, byte[] input, string inputExtension,
            string outputExtension, IEnumerable<string> extraArgs, CancellationToken ct)
        {
            var folder = Path.Combine(Path.GetTempPath(), "jularr-jxl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var inputFile = Path.Combine(folder, "source" + inputExtension);
            var outputFile = Path.Combine(folder, "result" + outputExtension);

            try
            {
                await File.WriteAllBytesAsync(inputFile, input, ct);
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    WorkingDirectory = folder
                };
                process.StartInfo.ArgumentList.Add(inputFile);
                process.StartInfo.ArgumentList.Add(outputFile);
                foreach (var arg in extraArgs) process.StartInfo.ArgumentList.Add(arg);
                try
                {
                    if (!process.Start()) return null;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(Timeout);
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        ct.ThrowIfCancellationRequested();
                        return null;
                    }

                    if (process.ExitCode != 0) return null;
                }
                catch (global::System.ComponentModel.Win32Exception)
                {
                    // Non-container developers can run Jularr without cjxl; WebP remains functional.
                    return null;
                }

                var file = new FileInfo(outputFile);
                if (!file.Exists || file.Length is < 32 or > MaximumOutputBytes) return null;
                return await File.ReadAllBytesAsync(outputFile, ct);
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (IOException) { /* temporary OS handles may still be closing */ }
                catch (UnauthorizedAccessException) { /* outside the service's managed data */ }
            }
        }
    }
}
