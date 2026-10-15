using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.OpenLibrary;
using Jularr.Integrations.Logic.OpenLibrary;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // Open Library lookups and imports for books and light novels.
    internal sealed class BookMetadata(OpenLibraryClient openLibrary)
    {
        public const string Namespace = "openlibrary";

        public static bool Handles(MediaType mediaType) => mediaType == MediaType.Book;

        // Open Library's trending list is global, not regional.
        public async Task<IReadOnlyList<OpenLibraryResult>> ReadTrendingAsync(CancellationToken ct)
        {
            try
            {
                return await openLibrary.ReadTrendingAsync(ct);
            }
            catch (OpenLibraryException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<IReadOnlyList<OpenLibraryResult>> SearchAsync(string query, int? year, CancellationToken ct)
        {
            try
            {
                var results = await openLibrary.SearchAsync(query, 20, ct);
                return year is { } wanted ? [.. results.OrderByDescending(result => result.Year == wanted)] : results;
            }
            catch (OpenLibraryException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<long> ImportAsync(LogicContext context, MediaSubtype? subtype, string workId, CancellationToken ct)
        {
            // Read before the short write batch.
            OpenLibraryWork work;
            try
            {
                work = await openLibrary.ReadWorkAsync(workId, ct);
            }
            catch (OpenLibraryException exception)
            {
                throw Translate(exception);
            }

            var facts = new[]
            {
                new { Key = "Author", Value = TextLimit.Cut(string.Join(", ", work.Authors), 4000), Localized = false },
                new { Key = "Overview", Value = TextLimit.Cut(work.Description, 4000), Localized = false },
                new { Key = "Poster", Value = OpenLibraryClient.CoverUrl(work.CoverId), Localized = false }
            }.Where(fact => !string.IsNullOrWhiteSpace(fact.Value));
            var data = new WorkMetadataData(MediaType.Book, subtype, Namespace, work.WorkId, TextLimit.Cut(work.Title, 500)!, null, work.Year is >= 1800 and <= 2200 ? work.Year : null, "[]", "[]",
                JsonSerializer.Serialize(facts), Namespace, null);
            return await context.ExecuteBatchAsync((ctx, token) => ctx.SaveWorkMetadataAsync(data, token), ct);
        }

        private static Exception Translate(OpenLibraryException exception) => exception.Failure switch
        {
            OpenLibraryFailure.NotFound => new KeyNotFoundException("Open Library does not know this work."),
            OpenLibraryFailure.RateLimited => new ServiceUnavailableException("metadata_rate_limited"),
            _ => new ServiceUnavailableException("metadata_unavailable")
        };
    }
}
