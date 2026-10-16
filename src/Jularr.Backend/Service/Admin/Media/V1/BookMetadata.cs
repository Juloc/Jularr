using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Hardcover;
using Jularr.Integrations.Data.OpenLibrary;
using Jularr.Integrations.Logic.Hardcover;
using Jularr.Integrations.Logic.OpenLibrary;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // Open Library lookups and imports for books and light novels; Hardcover, where it has a token, names their series.
    internal sealed class BookMetadata(OpenLibraryClient openLibrary, HardcoverClient hardcover, IDataProtectionProvider protection)
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

        public async Task<OpenLibraryWork> ReadAsync(string workId, CancellationToken ct)
        {
            try
            {
                return await openLibrary.ReadWorkAsync(workId, ct);
            }
            catch (OpenLibraryException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<long> ImportAsync(LogicContext context, MediaSubtype? subtype, string workId, CancellationToken ct)
        {
            // Read before the short write batch.
            var work = await ReadAsync(workId, ct);

            var facts = new[]
            {
                new { Key = "Overview", Value = TextLimit.Cut(work.Description, 4000), Localized = false },
                new { Key = "Poster", Value = OpenLibraryClient.CoverUrl(work.CoverId), Localized = false }
            }.Where(fact => !string.IsNullOrWhiteSpace(fact.Value));
            var data = new WorkMetadataData(MediaType.Book, subtype, Namespace, work.WorkId, TextLimit.Cut(work.Title, 500)!, null, work.Year is >= 1800 and <= 2200 ? work.Year : null, "[]", "[]",
                JsonSerializer.Serialize(facts), Namespace, null);
            // Editions are a bonus: without them the book is still the book.
            IReadOnlyList<OpenLibraryEdition> found;
            try
            {
                found = await openLibrary.ReadEditionsAsync(workId, ct);
            }
            catch (OpenLibraryException)
            {
                found = [];
            }

            var editions = WorkEditionLogic.Choose(found);
            data = new WorkMetadataData(data.MediaTypeId, data.MediaSubtypeId, data.Namespace, data.Value, data.CanonicalTitle, data.OriginalTitle, data.ReleaseYear, data.ExternalIdentities,
                data.Units, data.Facts, data.ProviderKey, data.ReleasedOn)
            {
                Translations = JsonSerializer.Serialize(editions.Where(item => item.Edition.Title is not null && item.Language.Length == 2)
                    .Select(item => new { item.Language, Region = (string?)null, item.Edition.Title, Overview = (string?)null })),
                OriginalLanguage = found.Where(edition => edition.Language is not null).OrderBy(edition => edition.Year ?? int.MaxValue)
                    .Select(edition => LanguageCodes.ToTwoLetter(edition.Language)).FirstOrDefault(code => code is { Length: 2 })
            };
            var series = await ReadSeriesAsync(context, found, ct);
            var people = WorkPeopleLogic.Rows(work.People)
                .Concat(editions.SelectMany(item => WorkPeopleLogic.Rows(item.Edition.People, editionLanguage: item.Language, editionFormat: item.Format))).ToArray();
            return await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                var savedId = await ctx.SaveWorkMetadataAsync(data, token);
                await ctx.SaveWorkEditionsAsync(WorkEditionLogic.Data(savedId, editions), token);
                await ctx.SaveWorkFranchiseAsync(new WorkFranchiseData(savedId, series?.Namespace, series?.ExternalId, series?.Name, series?.Position), token);
                await ctx.SaveWorkPeopleAsync(WorkPeopleLogic.Data(savedId, Namespace, people), token);
                return savedId;
            }, ct);
        }

        private async Task<(string Namespace, string ExternalId, string Name, decimal? Position)?> ReadSeriesAsync(LogicContext context, IReadOnlyList<OpenLibraryEdition> editions,
            CancellationToken ct)
        {
            if (await ConnectHardcoverAsync(context, ct) is { } connection)
            {
                try
                {
                    if ((await hardcover.ReadSeriesAsync(connection, [.. editions.SelectMany(edition => edition.Isbns).Distinct(StringComparer.Ordinal)], ct)).FirstOrDefault() is { } found)
                    {
                        return ("hardcover", found.Id.ToString(CultureInfo.InvariantCulture), TextLimit.Cut(found.Name, 300)!, found.Position);
                    }
                }
                catch (HardcoverException)
                {
                    // Open Library's series text stays.
                }
            }

            // Open Library's series text also names publishers' lines ("Penguin Classics"); only a numbered one is a series.
            return editions.SelectMany(edition => edition.Series).Select(FranchiseLogic.ReadSeries).OfType<(string Name, decimal? Position)>()
                .Where(item => item.Position is not null).Select(item => ((string Namespace, string ExternalId, string Name, decimal? Position)?)("openlibrary",
                    FranchiseLogic.SeriesKey(item.Name), TextLimit.Cut(item.Name, 300)!, item.Position)).FirstOrDefault();
        }

        // Hardcover takes part once its token is stored and it is switched on.
        private async Task<HardcoverConnection?> ConnectHardcoverAsync(LogicContext context, CancellationToken ct)
        {
            var credential = await context.ExecuteBatchAsync((ctx, token) => ctx.ReadProviderCredentialAsync(new ProviderParameters("hardcover"), token), ct);
            if (credential is not { IsEnabled: true, ProtectedSecret: { } secret })
            {
                return null;
            }

            byte[] clear;
            try
            {
                clear = protection.CreateProtector(SaveProviderSecretV1.ProtectorPurpose).Unprotect(secret);
            }
            catch (CryptographicException)
            {
                return null;
            }

            try
            {
                return new HardcoverConnection(Encoding.UTF8.GetString(clear));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }

        private static Exception Translate(OpenLibraryException exception) => exception.Failure switch
        {
            OpenLibraryFailure.NotFound => new KeyNotFoundException("Open Library does not know this work."),
            OpenLibraryFailure.RateLimited => new ServiceUnavailableException("metadata_rate_limited"),
            _ => new ServiceUnavailableException("metadata_unavailable")
        };
    }
}
