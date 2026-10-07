using System.Net;
using System.Text;
using Jularr.Web.Data;
using Jularr.Web.Features.Books;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Jularr.Tests;

/// <summary>
/// Books Discovery v2 (#371): honestly-sourced Trending/Popular/New rows and the optional
/// Hardcover rating enrichment. <see cref="BookCatalogServiceTests"/> already covers the
/// pre-existing Trending path (Open Library <c>/trending</c> enriched with Google Books covers);
/// these tests cover what #371 adds: the Popular and New sources, that they are never a disguised
/// copy of Trending or Gutenberg downloads, and the optional/owner-gated Hardcover integration.
/// </summary>
[TestClass]
public sealed class BookCatalogDiscoveryTests
{
    [TestMethod]
    public async Task PopularModeUsesOpenLibraryEditionCountNotTrendingEndpoint()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);

            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                if (request.RequestUri?.Host == "openlibrary.org")
                {
                    // Popular must never hit /trending (that would make it a duplicate of the
                    // Trending row) and must ask Open Library for its own edition-count ranking.
                    Assert.IsFalse(
                        (request.RequestUri.AbsolutePath ?? "").Contains("/trending/"),
                        "Popular must not reuse the Trending endpoint.");
                    StringAssert.Contains(request.RequestUri.Query, "sort=editions");

                    return JsonResponse("""
                        {
                          "docs": [
                            {
                              "key": "/works/OL66554W",
                              "title": "Pride and Prejudice",
                              "author_name": ["Jane Austen"],
                              "cover_i": 14348537,
                              "first_publish_year": 1813,
                              "subject": ["Fiction"],
                              "edition_count": 4042
                            }
                          ]
                        }
                        """);
                }

                if (request.RequestUri?.Host == "www.googleapis.com")
                {
                    // No confident match: Popular still returns the Open Library item as-is.
                    return JsonResponse("""{ "items": [] }""");
                }

                throw new AssertFailedException($"Unexpected request: {request.RequestUri}");
            }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var service = NewService(db, client);
            var books = await service.BrowseAsync(BookBrowseMode.Popular, CancellationToken.None);

            Assert.AreEqual(1, books.Count);
            Assert.AreEqual("ol-OL66554W", books[0].Id);
            Assert.AreEqual("Open Library", books[0].SourceName);
            Assert.AreEqual(4042, books[0].EditionCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task EveryBrowseFeedContinuesWhereThePreviousPageEndedThroughTheOpenLibraryOffset()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var seen = new List<string>();
            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                if (request.RequestUri?.Host == "openlibrary.org")
                {
                    seen.Add(request.RequestUri.PathAndQuery);
                    return JsonResponse("""{ "docs": [], "works": [] }""");
                }

                return JsonResponse("""{ "items": [] }""");
            }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var service = NewService(db, client);
            await service.BrowseAsync(BookBrowseMode.Popular, CancellationToken.None, offset: 48, limit: 24);
            await service.BrowseAsync(BookBrowseMode.New, CancellationToken.None, offset: 24, limit: 24);
            await service.BrowseAsync(BookBrowseMode.Trending, CancellationToken.None, offset: 72, limit: 24);

            Assert.IsTrue(seen.Any(address => address.Contains("sort=editions") && address.Contains("limit=24") && address.Contains("offset=48")), "Popular continues at its offset.");
            Assert.IsTrue(seen.Any(address => address.Contains("/subjects/fiction.json") && address.Contains("offset=24")), "New continues at its offset.");
            Assert.IsTrue(seen.Any(address => address.Contains("/trending/daily.json") && address.Contains("offset=72")), "Trending continues at its offset.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task NewModeUsesOpenLibrarySubjectsRecentListingNotTrendingOrEditions()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);

            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                if (request.RequestUri?.Host == "openlibrary.org")
                {
                    StringAssert.Contains(request.RequestUri.AbsolutePath, "/subjects/fiction.json");
                    StringAssert.Contains(request.RequestUri.Query, "sort=new");

                    return JsonResponse("""
                        {
                          "works": [
                            {
                              "key": "/works/OL1W",
                              "title": "A Brand New Novel",
                              "authors": [{ "name": "New Author" }],
                              "cover_id": 555,
                              "first_publish_year": 2026,
                              "subject": ["Fiction"],
                              "edition_count": 1
                            }
                          ]
                        }
                        """);
                }

                if (request.RequestUri?.Host == "www.googleapis.com")
                {
                    return JsonResponse("""{ "items": [] }""");
                }

                throw new AssertFailedException($"Unexpected request: {request.RequestUri}");
            }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var service = NewService(db, client);
            var books = await service.BrowseAsync(BookBrowseMode.New, CancellationToken.None);

            Assert.AreEqual(1, books.Count);
            Assert.AreEqual("A Brand New Novel", books[0].Title);
            Assert.AreEqual("New Author", books[0].Author);
            Assert.AreEqual(2026, books[0].FirstPublishYear);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task NewModeDropsImplausiblePublishYearsAndMarkupTitles()
    {
        // #371 found that Open Library's unscoped catalog query can surface spam/placeholder rows
        // (a first_publish_year of 9999, an <iframe> injected into a title); the subject-scoped
        // listing is used instead, and this defends against the same class of bad data regardless.
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);

            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                if (request.RequestUri?.Host == "openlibrary.org")
                {
                    return JsonResponse("""
                        {
                          "works": [
                            {
                              "key": "/works/OLSPAMW",
                              "title": "<iframe src=\"evil\"></iframe>",
                              "authors": [],
                              "first_publish_year": 9999,
                              "subject": []
                            },
                            {
                              "key": "/works/OLGOODW",
                              "title": "A Legitimate Recent Book",
                              "authors": [{ "name": "Real Author" }],
                              "first_publish_year": 2026,
                              "subject": ["Fiction"]
                            }
                          ]
                        }
                        """);
                }

                if (request.RequestUri?.Host == "www.googleapis.com")
                {
                    return JsonResponse("""{ "items": [] }""");
                }

                throw new AssertFailedException($"Unexpected request: {request.RequestUri}");
            }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var service = NewService(db, client);
            var books = await service.BrowseAsync(BookBrowseMode.New, CancellationToken.None);

            Assert.AreEqual(1, books.Count);
            Assert.AreEqual("A Legitimate Recent Book", books[0].Title);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task HardcoverDisabledByDefaultLeavesBrowseResultsUnchanged()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);

            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                Assert.AreNotEqual(
                    "api.hardcover.app",
                    request.RequestUri?.Host,
                    "Hardcover must never be called when no owner key is configured.");

                if (request.RequestUri?.Host == "openlibrary.org")
                {
                    return JsonResponse(TrendingWorkJson());
                }

                return JsonResponse("""{ "items": [] }""");
            }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            // No IDataProtectionProvider: the default constructor overload, exactly what every
            // other BookCatalogService caller/test already uses.
            var service = NewService(db, client);
            var books = await service.BrowseAsync(BookBrowseMode.Trending, CancellationToken.None);

            Assert.AreEqual(1, books.Count);
            Assert.IsNull(books[0].Rating);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task HardcoverEnabledAttachesRatingToAConfidentTitleAuthorMatch()
    {
        var path = TempDatabasePath();
        var keysRoot = new DirectoryInfo(Path.Combine(
            Path.GetTempPath(),
            "jularr-discovery-settings-" + Guid.NewGuid().ToString("N")));
        var keys = Directory.CreateDirectory(Path.Combine(keysRoot.FullName, "keys"));
        var data = Directory.CreateDirectory(Path.Combine(keysRoot.FullName, "data"));

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var protection = DataProtectionProvider.Create(keys);

            await new BookDiscoverySettingsStore(protection, data)
                .SaveAsync("hc_owner_token_123456789", CancellationToken.None);

            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                if (request.RequestUri?.Host == "openlibrary.org")
                {
                    return JsonResponse(TrendingWorkJson());
                }

                if (request.RequestUri?.Host == "api.hardcover.app")
                {
                    Assert.AreEqual(
                        "Bearer hc_owner_token_123456789",
                        request.Headers.Authorization?.ToString());

                    return JsonResponse("""
                        {
                          "data": {
                            "books": [
                              {
                                "title": "Dune",
                                "rating": 4.55,
                                "cached_contributors": [
                                  { "author": { "name": "Frank Herbert" }, "contribution": null }
                                ]
                              }
                            ]
                          }
                        }
                        """);
                }

                return JsonResponse("""{ "items": [] }""");
            }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var service = NewService(db, client, dataProtectionProvider: protection, discoveryDataDirectory: data);
            var books = await service.BrowseAsync(BookBrowseMode.Trending, CancellationToken.None);

            Assert.AreEqual(1, books.Count);
            Assert.AreEqual(4.55, books[0].Rating);
        }
        finally
        {
            File.Delete(path);
            if (keysRoot.Exists) keysRoot.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task HardcoverFailureDegradesGracefullyWithoutARating()
    {
        var path = TempDatabasePath();
        var keysRoot = new DirectoryInfo(Path.Combine(
            Path.GetTempPath(),
            "jularr-discovery-settings-" + Guid.NewGuid().ToString("N")));
        var keys = Directory.CreateDirectory(Path.Combine(keysRoot.FullName, "keys"));
        var data = Directory.CreateDirectory(Path.Combine(keysRoot.FullName, "data"));

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var protection = DataProtectionProvider.Create(keys);

            await new BookDiscoverySettingsStore(protection, data)
                .SaveAsync("hc_owner_token_123456789", CancellationToken.None);

            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                if (request.RequestUri?.Host == "openlibrary.org")
                {
                    return JsonResponse(TrendingWorkJson());
                }

                if (request.RequestUri?.Host == "api.hardcover.app")
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                return JsonResponse("""{ "items": [] }""");
            }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var service = NewService(db, client, dataProtectionProvider: protection, discoveryDataDirectory: data);
            var books = await service.BrowseAsync(BookBrowseMode.Trending, CancellationToken.None);

            Assert.AreEqual(1, books.Count);
            Assert.IsNull(books[0].Rating);
        }
        finally
        {
            File.Delete(path);
            if (keysRoot.Exists) keysRoot.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task DiscoverySettingsStoreProtectsKeyAndClearRemovesIt()
    {
        var root = new DirectoryInfo(Path.Combine(
            Path.GetTempPath(),
            "jularr-discovery-settings-" + Guid.NewGuid().ToString("N")));
        var keys = Directory.CreateDirectory(Path.Combine(root.FullName, "keys"));
        var data = Directory.CreateDirectory(Path.Combine(root.FullName, "data"));
        const string apiKey = "hc_super_secret_owner_key";

        try
        {
            var store = new BookDiscoverySettingsStore(DataProtectionProvider.Create(keys), data);

            var empty = await store.LoadAsync(CancellationToken.None);
            Assert.IsFalse(empty.HardcoverEnabled);

            await store.SaveAsync(apiKey, CancellationToken.None);
            var loaded = await store.LoadAsync(CancellationToken.None);
            Assert.IsTrue(loaded.HardcoverEnabled);
            Assert.AreEqual(apiKey, loaded.HardcoverApiKey);

            var persisted = await File.ReadAllTextAsync(
                Path.Combine(data.FullName, "books", "discovery-settings.json"));
            Assert.IsFalse(persisted.Contains(apiKey, StringComparison.Ordinal));

            await store.ClearAsync(CancellationToken.None);
            var cleared = await store.LoadAsync(CancellationToken.None);
            Assert.IsFalse(cleared.HardcoverEnabled);
        }
        finally
        {
            if (root.Exists)
            {
                root.Delete(recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ABrowseRowWhoseCatalogDidNotAnswerFailsInsteadOfBeingAnEmptyRow()
    {
        var path = TempDatabasePath();
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            using var client = new HttpClient(new DelegateHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))) { BaseAddress = new Uri("https://gutendex.com/") };
            var service = NewService(db, client);

            foreach (var mode in new[] { BookBrowseMode.Trending, BookBrowseMode.Popular, BookBrowseMode.New })
            {
                await Assert.ThrowsExactlyAsync<HttpRequestException>(() => service.BrowseAsync(mode, CancellationToken.None), mode.ToString());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task TrendingFallsBackToTheWeeklyWindowWhenTheDailyOneDidNotAnswer()
    {
        var path = TempDatabasePath();
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                if (request.RequestUri?.Host == "openlibrary.org")
                {
                    return request.RequestUri.AbsolutePath.Contains("/daily", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.BadGateway) : JsonResponse(TrendingWorkJson());
                }

                return JsonResponse("""{ "items": [] }""");
            })) { BaseAddress = new Uri("https://gutendex.com/") };

            var books = await NewService(db, client).BrowseAsync(BookBrowseMode.Trending, CancellationToken.None);

            Assert.AreEqual(1, books.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ACatalogSearchFailsOnlyWhenNoCatalogAnsweredAndTheTextSearchKeepsItsGutenbergFallback()
    {
        var path = TempDatabasePath();
        try
        {
            await using var db = await CreateDatabaseAsync(path);
            using var down = new HttpClient(new DelegateHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))) { BaseAddress = new Uri("https://gutendex.com/") };
            using var partial = new HttpClient(new DelegateHttpMessageHandler(request => request.RequestUri?.Host == "openlibrary.org"
                ? JsonResponse("""{ "docs": [ { "key": "/works/OL1W", "title": "Dune", "author_name": ["Frank Herbert"], "first_publish_year": 1965 } ] }""")
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))) { BaseAddress = new Uri("https://gutendex.com/") };

            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => NewService(db, down).SearchPrimaryCatalogsAsync("dune", CancellationToken.None));
            var fallback = await NewService(db, down).SearchAsync("dune", CancellationToken.None);
            var answered = await NewService(db, partial).SearchPrimaryCatalogsAsync("dune", CancellationToken.None);

            Assert.AreEqual(0, fallback.Count, "The plain search has always meant nothing found when every source failed.");
            Assert.AreEqual(1, answered.Count, "One catalog that answers is enough: the others contribute nothing.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TrendingWorkJson() => """
        {
          "works": [
            {
              "key": "/works/OL45804W",
              "title": "Dune",
              "author_name": ["Frank Herbert"],
              "cover_i": 15194431,
              "first_publish_year": 1965,
              "subject": ["Science fiction"],
              "isbn": ["9780593099322"]
            }
          ]
        }
        """;

    private static BookCatalogService NewService(
        AppDbContext db,
        HttpClient client,
        IDataProtectionProvider? dataProtectionProvider = null,
        DirectoryInfo? discoveryDataDirectory = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Books:Translation:MemoryPath"] = Path.Combine(
                    Path.GetTempPath(),
                    "jularr-book-discovery-tests",
                    Guid.NewGuid().ToString("N"))
            })
            .Build();

        return new BookCatalogService(
            client,
            db,
            new NoopBookTranslator(),
            config,
            dataProtectionProvider,
            discoveryDataDirectory);
    }

    private static string TempDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"jularr-books-discovery-{Guid.NewGuid():N}.db");

    private static async Task<AppDbContext> CreateDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;

        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class NoopBookTranslator : IBookTranslator
    {
        public string Id => "noop-books";

        public Task<string> TranslateLiteraryAsync(
            string sourceText,
            string sourceLanguage,
            string targetLanguage,
            string context,
            CancellationToken cancellationToken) =>
            Task.FromResult(sourceText);
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
