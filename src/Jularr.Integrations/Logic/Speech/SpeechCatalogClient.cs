using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.Speech;

namespace Jularr.Integrations.Logic.Speech
{
    internal sealed partial class SpeechCatalogClient(HttpClient http)
    {
        internal const string Releases = "https://api.github.com/repos/k2-fsa/sherpa-onnx/releases/tags/tts-models";
        internal const string Voices = "https://huggingface.co/rhasspy/piper-voices/raw/main/voices.json";
        private const string PackageRoot = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/";

        [GeneratedRegex(@"\A[a-z]{2,3}_[A-Z]{2}-[a-zA-Z0-9_]+-(?:x_low|low|medium|high)\z")]
        private static partial Regex ModelKey();

        [GeneratedRegex(@"\Asha256:([0-9a-f]{64})\z")]
        private static partial Regex Digest();

        internal async Task<IReadOnlyList<SpeechCatalogPackage>> ReadAsync(CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var releases = ReadJsonAsync(Releases, 8 * 1024 * 1024, timeout.Token);
            var voices = ReadJsonAsync(Voices, 2 * 1024 * 1024, timeout.Token);
            await Task.WhenAll(releases, voices);
            return Parse(await releases, await voices);
        }

        // Only the converted Piper/VITS packages fit the installed browser engine. The original
        // ONNX files and other sherpa engines are not interchangeable with these archives.
        internal static IReadOnlyList<SpeechCatalogPackage> Parse(string releaseJson, string voicesJson)
        {
            using var release = JsonDocument.Parse(releaseJson, new JsonDocumentOptions { MaxDepth = 24 });
            using var voices = JsonDocument.Parse(voicesJson, new JsonDocumentOptions { MaxDepth = 12 });
            if (release.RootElement.ValueKind != JsonValueKind.Object || !release.RootElement.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 4096 || voices.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Invalid speech source catalog.");
            }

            var result = new List<SpeechCatalogPackage>();
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asset in assets.EnumerateArray())
            {
                var filename = Text(asset, "name");
                if (filename is null || !filename.StartsWith("vits-piper-", StringComparison.Ordinal) || !filename.EndsWith(".tar.bz2", StringComparison.Ordinal)) continue;
                var key = filename[11..^8];
                if (!ModelKey().IsMatch(key) || !voices.RootElement.TryGetProperty(key, out var voice) || Text(voice, "key") != key
                    || Text(asset, "browser_download_url") != PackageRoot + filename || Text(asset, "digest") is not { } digest
                    || !Digest().IsMatch(digest) || !asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes)
                    || bytes is < 1 or > 134_217_728 || !voice.TryGetProperty("num_speakers", out var speakers) || speakers.ValueKind != JsonValueKind.Number || !speakers.TryGetInt32(out var count) || count < 1
                    || !voice.TryGetProperty("language", out var language) || Text(language, "code") is not { } locale
                    || !key.StartsWith(locale + "-", StringComparison.Ordinal) || Text(language, "family") is not { } family
                    || Text(voice, "name") is not { Length: > 0 and <= 80 } name || Text(voice, "quality") is not { } quality
                    || key != locale + "-" + name + "-" + quality) continue;

                var card = family + "/" + locale + "/" + name + "/" + quality + "/MODEL_CARD";
                if (family != locale.Split('_')[0] || !voice.TryGetProperty("files", out var files) || !files.TryGetProperty(card, out _)) continue;
                var hash = digest[7..];
                if (!hashes.Add(hash)) continue;
                result.Add(new SpeechCatalogPackage(key, name + " (" + quality + ")", locale.Replace('_', '-'),
                    "https://huggingface.co/rhasspy/piper-voices/blob/main/" + card, PackageRoot + filename,
                    "https://k2-fsa.github.io/sherpa/onnx/tts/all/piper/mp3/" + locale + "/vits-piper-" + key + "/0.mp3", bytes, hash));
            }

            if (result.Count is 0 or > 1024) throw new InvalidDataException("The source returned no compatible, hash-pinned packages or too many packages.");
            return result;
        }

        private async Task<string> ReadJsonAsync(string address, int limit, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("Jularr/1.0 (+https://github.com/Juloc/Jularr)");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Speech source metadata is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) != 0)
            {
                if (output.Length + read > limit) throw new InvalidDataException("Speech source metadata is too large.");
                output.Write(buffer, 0, read);
            }

            return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, (int)output.Length);
        }

        private static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
