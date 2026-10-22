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
            return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 16 }, ct);
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

        internal async Task PullAsync(Uri server, string model, CancellationToken ct)
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
                if (step.RootElement.TryGetProperty("error", out var error))
                    throw new InvalidDataException("Ollama model download failed: " + error.GetString());
            }
            if (!(await InstalledAsync(server, ct)).Contains(model, StringComparer.Ordinal))
                throw new InvalidDataException("Ollama did not register the downloaded model.");
        }

        internal async Task<(string Text, double Seconds, double TokensPerSecond)> TranslateAsync(Uri server, string model,
            string source, string target, string text, CancellationToken ct)
        {
            if (!Supported(model)) throw new ArgumentException("Unknown approved model.", nameof(model));
            if (source.Length is < 2 or > 3 || target.Length is < 2 or > 3
                || !source.All(char.IsAsciiLetterLower) || !target.All(char.IsAsciiLetterLower))
                throw new ArgumentException("Unsupported language identifier.");
            if (string.IsNullOrWhiteSpace(text) || text.Length > 2400) throw new ArgumentException("Translation unit out of bounds.");
            var prompt = $"You are a professional {source} to {target} translator. Translate accurately, preserving all details, names and tone. "
                + $"Output ONLY the {target} translation. Translate the following {source} text into {target}:\n\n\n{text}";
            var timer = Stopwatch.StartNew();
            using var response = await JsonAsync(server, "api/generate",
                new { model, prompt, stream = false, keep_alive = "0", options = new { temperature = 0, num_ctx = 3072, num_predict = 1536 } }, ct);
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
