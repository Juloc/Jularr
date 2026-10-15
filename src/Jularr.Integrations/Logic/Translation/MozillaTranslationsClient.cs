using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Jularr.Integrations.Data.Translation;

namespace Jularr.Integrations.Logic.Translation
{
    // Mozilla's Remote Settings lists the language models and Bergamot engine builds Firefox translates with; files come from its attachment host.
    internal sealed class MozillaTranslationsClient(HttpClient http)
    {
        public static readonly Uri Settings = new("https://firefox.settings.services.mozilla.com/v1/buckets/main/collections/");
        public static readonly Uri Attachments = new("https://firefox-settings-attachments.cdn.mozilla.net/");
        public const long MaximumBytes = 200L * 1024 * 1024;

        // The first model collection; its models run on the engine builds published beside it.
        public Task<IReadOnlyList<TranslationFile>> ReadModelsAsync(CancellationToken ct) => ReadAsync("translations-models", ct);

        public Task<IReadOnlyList<TranslationFile>> ReadEnginesAsync(CancellationToken ct) => ReadAsync("translations-wasm", ct);

        // Written beside the destination first; only content with the published size and hash takes its place.
        public async Task DownloadAsync(TranslationFile file, string destination, CancellationToken ct)
        {
            if (file.Size > MaximumBytes)
            {
                throw new TranslationSourceException("The translation file is too large.");
            }

            var partial = destination + ".partial";
            try
            {
                using (var response = await SendAsync(new Uri(Attachments, file.Location), ct))
                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    long written = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        written += read;
                        if (written > file.Size)
                        {
                            throw new TranslationSourceException("The translation file is larger than published.");
                        }

                        hash.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    }

                    if (written != file.Size || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Hash, StringComparison.Ordinal))
                    {
                        throw new TranslationSourceException("The translation file does not match its published hash.");
                    }
                }

                File.Move(partial, destination, overwrite: true);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new TranslationSourceException("The translation file could not be downloaded.", exception);
            }
            finally
            {
                File.Delete(partial);
            }
        }

        private async Task<IReadOnlyList<TranslationFile>> ReadAsync(string collection, CancellationToken ct)
        {
            using var response = await SendAsync(new Uri(Settings, collection + "/records"), ct);
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!document.RootElement.TryGetProperty("data", out var records) || records.ValueKind != JsonValueKind.Array)
                {
                    throw new TranslationSourceException("Remote Settings returned no records.");
                }

                var files = new List<TranslationFile>();
                foreach (var record in records.EnumerateArray())
                {
                    if (Text(record, "name") is not { } name || Text(record, "version") is not { } version || !record.TryGetProperty("attachment", out var attachment)
                        || attachment.ValueKind != JsonValueKind.Object || Text(attachment, "hash") is not { Length: 64 } hash || Text(attachment, "location") is not { } location
                        || !attachment.TryGetProperty("size", out var size) || !size.TryGetInt64(out var bytes))
                    {
                        continue;
                    }

                    files.Add(new TranslationFile(name, version, Text(record, "fileType"), Text(record, "fromLang"), Text(record, "toLang"), hash.ToLowerInvariant(), bytes,
                        location, Text(record, "filter_expression") is not null));
                }

                return files;
            }
            catch (JsonException exception)
            {
                throw new TranslationSourceException("Remote Settings returned invalid JSON.", exception);
            }
        }

        private async Task<HttpResponseMessage> SendAsync(Uri address, CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new TranslationSourceException("Mozilla's translation files could not be reached.", exception);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                response.Dispose();
                throw new TranslationSourceException("Mozilla answered HTTP " + (int)response.StatusCode + ".");
            }

            return response;
        }

        private static string? Text(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text ? text : null;
    }
}
