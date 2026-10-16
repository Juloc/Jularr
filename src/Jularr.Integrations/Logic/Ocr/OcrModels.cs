using System.Net;
using System.Security.Cryptography;

namespace Jularr.Integrations.Logic.Ocr
{
    // RapidOCR's PP-OCRv5 mobile models as ONNX, pinned by size and SHA-256: one detection model and a recognition model per script.
    public static class OcrModels
    {
        public static readonly OcrModel Detection = new("onnx/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx", 4_819_576, "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae");

        private static readonly OcrModel Latin = Recognizer("latin", 7_904_513, "b20bd37c168a570f583afbc8cd7925603890efbcdc000a59e22c269d160b5f5a");
        private static readonly OcrModel Chinese = Recognizer("ch", 16_631_306, "5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5");
        private static readonly OcrModel Korean = Recognizer("korean", 13_488_748, "cd6e2ea50f6943ca7271eb8c56a877a5a90720b7047fe9c41a2e541a25773c9b");
        private static readonly OcrModel EastSlavic = Recognizer("eslav", 7_911_802, "08705d6721849b1347d26187f15a5e362c431963a2a62bfff4feac578c489aab");
        private static readonly OcrModel Cyrillic = Recognizer("cyrillic", 8_074_092, "90f761b4bfcce0c8c561c0cb5c887b0971d3ec01c32164bdf7374a35b0982711");
        private static readonly OcrModel Greek = Recognizer("el", 7_832_350, "b4368bccd557123c702b7549fee6cd1e94b581337d1c9b65310f109131542b7f");
        private static readonly OcrModel Arabic = Recognizer("arabic", 8_023_828, "c1192e632d0baa9146ae5b756a0e635e3dc63c1733737ebfd1629e87144e9295");
        private static readonly OcrModel Devanagari = Recognizer("devanagari", 7_940_361, "d6f0a906580e3fa6b324a318718f1f31f268b6ea8ef985f91c2012a37f52c91e");
        private static readonly OcrModel Thai = Recognizer("th", 7_915_294, "de541dd83161c241ff426f7ecfd602a0ba77d686cf3ab9a6c255ea82fd08006e");
        private static readonly OcrModel Tamil = Recognizer("ta", 7_909_926, "a42448808b7dea87597336f12438935f40353f1949e8360acd9e06b4da21bfe1");
        private static readonly OcrModel Telugu = Recognizer("te", 7_923_102, "a3690451b50028a09a3316a1274f7c05728151ea3f8fd392696397a7fefcbd92");

        // A book's language chooses its script; Latin reads most and unknown languages.
        public static OcrModel Recognition(string? language) => language?.Split('-')[0].ToLowerInvariant() switch
        {
            "zh" or "ja" => Chinese,
            "ko" => Korean,
            "ru" or "uk" or "be" => EastSlavic,
            "bg" or "sr" or "mk" or "kk" or "ky" or "mn" or "tg" => Cyrillic,
            "el" => Greek,
            "ar" or "fa" or "ur" or "ps" or "ug" => Arabic,
            "hi" or "mr" or "ne" or "sa" => Devanagari,
            "th" => Thai,
            "ta" => Tamil,
            "te" => Telugu,
            _ => Latin
        };

        private static OcrModel Recognizer(string script, long size, string hash) => new("onnx/PP-OCRv5/rec/" + script + "_PP-OCRv5_rec_mobile.onnx", size, hash);
    }

    public sealed record OcrModel(string Location, long Size, string Hash)
    {
        public string FileName => Path.GetFileName(Location);
    }

    // The model host; files are written beside their destination first and take its place only with the pinned size and hash.
    internal sealed class OcrModelClient(HttpClient http)
    {
        public async Task DownloadAsync(OcrModel model, string destination, CancellationToken ct)
        {
            var partial = destination + ".partial";
            try
            {
                using (var response = await http.GetAsync(new Uri(model.Location, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        throw new OcrSourceException("The model host answered HTTP " + (int)response.StatusCode + ".");
                    }

                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    long written = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        written += read;
                        if (written > model.Size)
                        {
                            throw new OcrSourceException("The model is larger than pinned.");
                        }

                        hash.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    }

                    if (written != model.Size || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), model.Hash, StringComparison.Ordinal))
                    {
                        throw new OcrSourceException("The model does not match its pinned hash.");
                    }
                }

                File.Move(partial, destination, overwrite: true);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new OcrSourceException("The model could not be downloaded.", exception);
            }
            finally
            {
                File.Delete(partial);
            }
        }
    }

    internal sealed class OcrSourceException(string message, Exception? innerException = null) : Exception(message, innerException);
}
