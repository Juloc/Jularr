using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Integrations.Data.Common;

namespace Jularr.Integrations.Logic.AcoustId
{
    // AcoustID's lookup: the MusicBrainz release groups of the recordings a fingerprint matches; three requests a second at most.
    internal sealed partial class AcoustIdClient(HttpClient http)
    {
        private const double MinimumScore = 0.8;
        private static readonly SemaphoreSlim Pace = new(1, 1);
        private static DateTime _lastRequest = DateTime.MinValue;

        public async Task<IReadOnlyList<string>> ReadReleaseGroupsAsync(string key, int durationSeconds, string fingerprint, CancellationToken ct)
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client"] = key,
                ["meta"] = "recordings releasegroups",
                ["duration"] = durationSeconds.ToString(CultureInfo.InvariantCulture),
                ["fingerprint"] = fingerprint
            });
            await Pace.WaitAsync(ct);
            HttpResponseMessage response;
            try
            {
                var wait = _lastRequest + TimeSpan.FromMilliseconds(340) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                }

                _lastRequest = DateTime.UtcNow;
                response = await http.PostAsync(new Uri("lookup", UriKind.Relative), content, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new ProviderUnavailableException();
            }
            finally
            {
                Pace.Release();
            }

            using (response)
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new ProviderUnavailableException();
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                return Parse(document.RootElement);
            }
        }

        // Release groups of every recording of a confident result.
        internal static IReadOnlyList<string> Parse(JsonElement root)
        {
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "ok" || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                throw new ProviderUnavailableException();
            }

            return [.. results.EnumerateArray()
                .Where(result => result.TryGetProperty("score", out var score) && score.TryGetDouble(out var value) && value >= MinimumScore)
                .SelectMany(result => Items(result, "recordings")).SelectMany(recording => Items(recording, "releasegroups"))
                .Select(group => group.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
                .OfType<string>().Where(id => Mbid().IsMatch(id)).Distinct(StringComparer.Ordinal).Take(50)];
        }

        private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
            element.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];

        [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
        private static partial Regex Mbid();
    }
}
