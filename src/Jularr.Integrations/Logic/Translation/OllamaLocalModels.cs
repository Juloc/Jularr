using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace Jularr.Integrations.Logic.Translation
{
    // Ollama owns its runtime and model volume. No binaries are embedded in Jularr.
    internal sealed class OllamaLocalModels(HttpClient http)
    {
        internal static readonly string[] Candidates = ["translategemma:4b", "translategemma:12b", "translategemma:27b"];
        internal static bool Supported(string model) => Candidates.Contains(model, StringComparer.Ordinal);
        internal static Uri? Endpoint(string? value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var address)
                || address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps
                || address.UserInfo.Length != 0 || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0)
            {
                return null;
            }

            // No remote-cloud endpoints or unrestricted user-supplied SSRF targets.
            if (address.Host is "localhost" or "ollama" or "127.0.0.1" or "::1") return address;
            if (!System.Net.IPAddress.TryParse(address.Host, out var ip)
                || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
            var bytes = ip.GetAddressBytes();
            return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168 ? address : null;
        }

        private async Task<JsonDocument> JsonAsync(Uri server, string path, object? input, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(input is null ? HttpMethod.Get : HttpMethod.Post, new Uri(server, path));
            if (input is not null) request.Content = JsonContent.Create(input);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 2_097_152) throw new InvalidDataException("Ollama response too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            // Do not trust Content-Length: chunked servers can omit it.
            using var buffer = new MemoryStream();
            var chunk = new byte[16_384];
            int count;
            while ((count = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + count > 2_097_152) throw new InvalidDataException("Oversized Ollama response.");
                buffer.Write(chunk, 0, count);
            }
            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 16 }, ct);
        }

        // Model tags are mutable: the digest participates in the cache identity.
        internal async Task<string> DigestAsync(Uri server, string model, CancellationToken ct)
        {
            if (!Supported(model)) throw new ArgumentException("Unknown approved model.", nameof(model));
            using var response = await JsonAsync(server, "api/tags", null, ct);
            if (!response.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Model catalog unavailable.");
            foreach (var item in models.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != model
                    || !item.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String) continue;
                var value = digest.GetString()!;
                if (value.Length == 64 && value.All(Uri.IsHexDigit)) return value.ToLowerInvariant();
            }
            throw new InvalidDataException("Model digest unavailable; refusing unversioned inference.");
        }

        // Split long paragraphs without generating standalone output blocks.
        // Prefer a sentence/space boundary; preserve source order and block identity.
        internal static IReadOnlyList<string> Segments(string value)
        {
            if (value.Length == 0 || value.Length > 20_000) throw new ArgumentException("Invalid source block length.", nameof(value));
            var parts = new List<string>();
            var remaining = value.Trim();
            while (remaining.Length > 2400)
            {
                var limit = 2200;
                if (char.IsHighSurrogate(remaining[limit - 1]) && char.IsLowSurrogate(remaining[limit])) limit--;
                var boundary = -1;
                for (var index = limit - 1; index >= limit / 2; index--)
                {
                    if (char.IsWhiteSpace(remaining[index]) || "。！？.!?;".Contains(remaining[index]))
                    {
                        boundary = index + 1;
                        break;
                    }
                }
                var cut = boundary > 0 ? boundary : limit;
                parts.Add(remaining[..cut].Trim());
                remaining = remaining[cut..].TrimStart();
            }
            if (remaining.Length > 0) parts.Add(remaining);
            return parts;
        }

        internal async Task<string[]> InstalledAsync(Uri server, CancellationToken ct)
        {
            using var response = await JsonAsync(server, "api/tags", null, ct);
            if (!response.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Ollama returned no model catalog.");
            return models.EnumerateArray().Where(x => x.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                .Select(x => x.GetProperty("name").GetString()!).Where(Supported).Distinct(StringComparer.Ordinal).ToArray();
        }

        internal async Task<string[]> RunningAsync(Uri server, CancellationToken ct)
        {
            using var response = await JsonAsync(server, "api/ps", null, ct);
            if (!response.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Ollama returned no active models.");
            return models.EnumerateArray().Where(x => x.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                .Select(x => x.GetProperty("name").GetString()!).Where(Supported).Distinct(StringComparer.Ordinal).ToArray();
        }

        internal async Task PullAsync(Uri server, string model, CancellationToken ct, Action<int>? progress = null)
        {
            if (!Supported(model)) throw new ArgumentException("Unknown approved model.", nameof(model));
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "api/pull"))
            {
                Content = JsonContent.Create(new { model, stream = true })
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (line.Length > 8192) throw new InvalidDataException("Ollama progress event too large.");
                using var step = JsonDocument.Parse(line);
                if (step.RootElement.TryGetProperty("total", out var total)
                    && step.RootElement.TryGetProperty("completed", out var completed)
                    && total.ValueKind == JsonValueKind.Number && completed.ValueKind == JsonValueKind.Number
                    && total.TryGetInt64(out var bytes) && bytes > 0 && completed.TryGetInt64(out var done))
                {
                    progress?.Invoke((int)Math.Clamp(done * 100.0 / bytes, 0, 100));
                }
                if (step.RootElement.TryGetProperty("error", out var error))
                    throw new InvalidDataException("Ollama model download failed: " + error.GetString());
            }
            if (!(await InstalledAsync(server, ct)).Contains(model, StringComparer.Ordinal))
                throw new InvalidDataException("Ollama did not register the downloaded model.");
        }

        // Basic safeguards: a translation must not be an echo, heavily untranslated Japanese, or an obvious truncation.
        internal static bool LooksComplete(string source, string output, string target)
        {
            if (string.IsNullOrWhiteSpace(output) || output.Length < Math.Min(3, source.Length / 12)
                || output.Length > 20_000 || output.Length > source.Length * 6 + 200) return false;
            if (source.Length > 16 && string.Equals(source.Trim(), output.Trim(), StringComparison.Ordinal)) return false;
            if (target is "de" or "en" or "fr" or "it" or "es" or "pt")
            {
                var japanese = output.Count(character => character is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff');
                if (japanese >= 8 && japanese * 3 > output.Length) return false;
            }
            return true;
        }

        internal async Task<(string Text, double Seconds, double TokensPerSecond)> TranslateAsync(Uri server, string model,
            string source, string target, string text, CancellationToken ct, string? previousContext = null)
        {
            if (!Supported(model)) throw new ArgumentException("Unknown approved model.", nameof(model));
            if (source.Length is < 2 or > 3 || target.Length is < 2 or > 3
                || !source.All(char.IsAsciiLetterLower) || !target.All(char.IsAsciiLetterLower))
                throw new ArgumentException("Unsupported language identifier.");
            if (string.IsNullOrWhiteSpace(text) || text.Length > 20_000) throw new ArgumentException("Translation unit out of bounds.");
            if (text.Length > 2400)
            {
                var output = new List<string>();
                double seconds = 0, chunkSpeed = 0;
                var context = previousContext ?? "";
                foreach (var part in Segments(text))
                {
                    var translated = await TranslateAsync(server, model, source, target, part, ct, context);
                    output.Add(translated.Text);
                    seconds += translated.Seconds;
                    chunkSpeed += translated.TokensPerSecond;
                    context = (context + "\n" + part).Trim();
                    if (context.Length > 800) context = context[^800..];
                }
                return (string.Join(" ", output), Math.Round(seconds, 2), Math.Round(chunkSpeed / output.Count, 2));
            }
            var sourceName = global::System.Globalization.CultureInfo.GetCultureInfo(source).EnglishName;
            var targetName = global::System.Globalization.CultureInfo.GetCultureInfo(target).EnglishName;
            // Keep the official TranslateGemma text delimiter, with context confined to the instruction.
            var guidance = "Preserve the narrator's point of view, speaker identity and character names. Do not invent or omit information.\n";
            if (!string.IsNullOrWhiteSpace(previousContext))
            {
                var context = previousContext.Trim();
                if (context.Length > 800) context = context[^800..];
                guidance += "Earlier source passage for context only (do not translate or repeat it):\n" + context + "\n";
            }
            var prompt = $"You are a professional {sourceName} ({source}) to {targetName} ({target}) translator. "
                + $"Your goal is to accurately convey the meaning and nuances of the original {sourceName} text while adhering to {targetName} grammar, vocabulary, and cultural sensitivities.\n"
                + guidance
                + $"Produce only the {targetName} translation, without any additional explanations or commentary. "
                + $"Please translate the following {sourceName} text into {targetName}:\n\n\n{text}";
            var timer = Stopwatch.StartNew();
            using var response = await JsonAsync(server, "api/generate",
                new { model, prompt, stream = false, keep_alive = "5m", options = new { temperature = 0, num_ctx = 4096, num_predict = 1536 } }, ct);
            timer.Stop();
            if (!response.RootElement.TryGetProperty("response", out var result) || result.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Ollama translation response was invalid.");
            var translation = result.GetString()!.Trim();
            if (translation.Length == 0 || translation.Length > 20_000) throw new InvalidDataException("Ollama returned incomplete text.");
            double speed = 0;
            if (response.RootElement.TryGetProperty("eval_count", out var count) && count.TryGetDouble(out var tokens)
                && response.RootElement.TryGetProperty("eval_duration", out var duration) && duration.TryGetDouble(out var nanoseconds) && nanoseconds > 0)
                speed = Math.Round(tokens * 1e9 / nanoseconds, 2);
            return (translation, Math.Round(timer.Elapsed.TotalSeconds, 2), speed);
        }
    }
}
