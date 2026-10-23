using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Integrations.Data.Hardcover;

namespace Jularr.Integrations.Logic.Hardcover
{
    // Hardcover's GraphQL API: the series of a book, found through the ISBNs of its editions.
    internal sealed class HardcoverClient(HttpClient http)
    {
        private const string SeriesQuery =
            "query ($isbns: [String!]) { editions(where: {_or: [{isbn_13: {_in: $isbns}}, {isbn_10: {_in: $isbns}}]}, limit: 1) "
            + "{ book { id book_series(order_by: {position: asc}) { position series { id name } } } } }";

        public async Task<IReadOnlyList<HardcoverSeries>> ReadSeriesAsync(HardcoverConnection connection, IReadOnlyList<string> isbns, CancellationToken ct)
        {
            if (isbns.Count == 0)
            {
                return [];
            }

            using var document = await PostAsync(connection, new { query = SeriesQuery, variables = new { isbns = isbns.Take(20) } }, ct);
            var editions = document.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("editions", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().ToArray()
                : throw new HardcoverException(HardcoverFailure.InvalidResponse, "Hardcover answered without editions.");
            if (editions.Length == 0 || !editions[0].TryGetProperty("book", out var book) || !book.TryGetProperty("book_series", out var memberships)
                || memberships.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. memberships.EnumerateArray().Select(membership =>
                membership.TryGetProperty("series", out var series) && series.ValueKind == JsonValueKind.Object
                    && series.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var seriesId)
                    && series.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString()!.Trim() is { Length: > 0 and <= 300 } title
                    ? new HardcoverSeries(seriesId, title, membership.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Number
                        && position.TryGetDecimal(out var place) && place is >= 0 and < 100000 ? place : null)
                    : null).OfType<HardcoverSeries>().Take(5)];
        }

        private async Task<JsonDocument> PostAsync(HardcoverConnection connection, object body, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("graphql", UriKind.Relative)) { Content = JsonContent.Create(body) };
            var token = connection.Token.Trim();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? token[7..].Trim() : token);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new HardcoverException(HardcoverFailure.Unavailable, "Hardcover could not be reached.", exception);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new HardcoverException(HardcoverFailure.Unauthorized, "Hardcover refused the token.");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new HardcoverException(HardcoverFailure.RateLimited, "Hardcover limits requests.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new HardcoverException(HardcoverFailure.Unavailable, "Hardcover failed.");
                }

                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                    {
                        document.Dispose();
                        throw new HardcoverException(HardcoverFailure.InvalidResponse, "Hardcover rejected the query.");
                    }

                    return document;
                }
                catch (JsonException exception)
                {
                    throw new HardcoverException(HardcoverFailure.InvalidResponse, "Hardcover returned invalid JSON.", exception);
                }
            }
        }
    }
}
