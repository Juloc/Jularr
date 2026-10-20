using System.Security.Cryptography;

namespace Jularr.Integrations.Logic.Speech
{
    // Only an explicitly activated, catalog-pinned package reaches this client.
    internal sealed class SpeechModelClient(HttpClient http)
    {
        public async Task DownloadAsync(Uri address, long sizeBytes, string sha256, string destination, CancellationToken ct)
        {
            if (address.Scheme != "https" || address.UserInfo.Length != 0 || sizeBytes is < 1 or > 536_870_912)
            {
                throw new InvalidDataException("Invalid speech package metadata.");
            }

            var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                using (var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is { } length && length != sizeBytes)
                    {
                        throw new InvalidDataException("The speech package has the wrong size.");
                    }

                    await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                    await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    long written = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, timeout.Token)) != 0)
                    {
                        written += read;
                        if (written > sizeBytes)
                        {
                            throw new InvalidDataException("The speech package is larger than published.");
                        }

                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    }

                    if (written != sizeBytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != sha256)
                    {
                        throw new InvalidDataException("The speech package does not match its published hash.");
                    }
                }

                File.Move(partial, destination, overwrite: true);
            }
            finally
            {
                File.Delete(partial);
            }
        }
    }
}
