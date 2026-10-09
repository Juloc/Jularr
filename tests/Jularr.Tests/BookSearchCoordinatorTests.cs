using System.Net;
using Jularr.Web.Features.Acquisition.Core;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Books;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class BookSearchCoordinatorTests
{
    [TestMethod]
    public async Task SearchCombinesEnabledOpdsAndUsenetAvailabilityIntoOneWork()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "jularr-book-search-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var settingsPath = Path.Combine(root, "opds-sources.json");
            var dbPath = Path.Combine(root, "search.db");
            await using var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={dbPath}")
                    .Options);

            using var client = new HttpClient(
                new DelegateHttpMessageHandler(request =>
                {
                    var uri = request.RequestUri
                        ?? throw new AssertFailedException("Request URI was missing.");

                    if (uri.Host == "catalog.example")
                    {
                        return JsonResponse(
                            """
                            {
                              "metadata": { "title": "Test OPDS" },
                              "publications": [
                                {
                                  "metadata": {
                                    "title": "Treasure Island",
                                    "author": [{"name": "Robert Louis Stevenson"}],
                                    "description": "Pirates and treasure.",
                                    "language": "en"
                                  },
                                  "links": [
                                    {
                                      "rel": "http://opds-spec.org/acquisition/open-access",
                                      "type": "application/epub+zip",
                                      "href": "/books/treasure.epub"
                                    }
                                  ]
                                }
                              ]
                            }
                            """);
                    }

                    // The metadata catalogs are deliberately unavailable. OPDS must still produce
                    // the canonical search result instead of the whole search failing.
                    throw new HttpRequestException("Catalog unavailable in test.");
                }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Books:Opds:SettingsPath"] = settingsPath
                    })
                .Build();
            var books = new BookCatalogService(
                client,
                db,
                new NoopBookTranslator(),
                configuration);

            await books.SaveOpdsSourceAsync(
                null,
                "Test OPDS",
                "https://catalog.example/opds",
                null,
                null,
                true,
                false,
                CancellationToken.None);

            var protection = new EphemeralDataProtectionProvider();
            var indexerStore = new IndexerStore(
                protection,
                new DirectoryInfo(root));
            await indexerStore.SaveAsync(
                new IndexerEntry(
                    Guid.NewGuid(),
                    "Books Indexer",
                    IndexerType.Newznab,
                    Enabled: true,
                    Priority: 1,
                    IndexerSettings.CreateDefault(
                        "https://indexer.example",
                        IndexerType.Newznab),
                    "secret"));

            var fakeIndexer = new FakeIndexer();
            var indexers = new IndexerSearchCoordinator(
                new Dictionary<IndexerType, IIndexer>
                {
                    [IndexerType.Newznab] = fakeIndexer
                },
                indexerStore,
                new AcquisitionHealthStore(new DirectoryInfo(root)),
                NullLogger<IndexerSearchCoordinator>.Instance);
            var coordinator = new BookSearchCoordinator(
                books,
                indexers,
                BookProfiles(root),
                NullLogger<BookSearchCoordinator>.Instance);

            var response = await coordinator.SearchAsync(
                "Treasure Island",
                CancellationToken.None);

            var result = Assert.ContainsSingle(response.Items);
            Assert.AreEqual("Treasure Island", result.Book.Title);
            Assert.AreEqual("Robert Louis Stevenson", result.Book.Author);
            Assert.IsTrue(result.Book.Identities.Any(identity =>
                identity.StartsWith("opds-", StringComparison.Ordinal)));
            Assert.IsFalse(result.Availability.DirectOrFree);
            Assert.IsTrue(result.Availability.Opds);
            Assert.IsTrue(result.Availability.Usenet);
            Assert.AreEqual(1, result.Availability.EligibleUsenetReleases);
            Assert.IsTrue(fakeIndexer.Searches > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void UsenetQueryPlanCoversEveryDisplayedCanonicalWork()
    {
        var works = Enumerable.Range(1, 24)
            .Select(index => new BookCatalogItem(
                $"book-{index}",
                $"Book {index:00}",
                $"Author {index:00}",
                null,
                null,
                [],
                null,
                null,
                null,
                $"https://catalog.example/book-{index}",
                "Test",
                null))
            .ToArray();

        var queries = BookSearchCoordinator.BuildUsenetQueries(
            works,
            "books");

        Assert.AreEqual(25, queries.Count);
        Assert.AreEqual("books", queries[0]);
        foreach (var index in Enumerable.Range(1, 24))
        {
            CollectionAssert.Contains(
                queries.ToList(),
                $"Author {index:00} Book {index:00}");
        }
    }

    [TestMethod]
    public async Task SearchChecksGutenbergEvenWhenMetadataAlreadyFoundTheWork()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "jularr-book-gutenberg-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={Path.Combine(root, "search.db")}")
                    .Options);

            using var client = new HttpClient(
                new DelegateHttpMessageHandler(request =>
                {
                    var uri = request.RequestUri
                        ?? throw new AssertFailedException("Request URI was missing.");

                    return uri.Host switch
                    {
                        "openlibrary.org" => JsonResponse(
                            """
                            {
                              "docs": [
                                {
                                  "key": "/works/OLTREASUREW",
                                  "title": "Treasure Island",
                                  "author_name": ["Robert Louis Stevenson"],
                                  "first_publish_year": 1883,
                                  "edition_count": 100
                                }
                              ]
                            }
                            """),
                        "www.googleapis.com" => JsonResponse("""{ "items": [] }"""),
                        "id.wikisource.org" => JsonResponse("""{ "query": { "search": [] } }"""),
                        "gutendex.com" => JsonResponse(
                            """
                            {
                              "results": [
                                {
                                  "id": 120,
                                  "title": "Treasure Island",
                                  "authors": [{"name": "Stevenson, Robert Louis"}],
                                  "subjects": ["Adventure stories"],
                                  "summaries": [],
                                  "formats": {
                                    "application/epub+zip": "https://www.gutenberg.org/ebooks/120.epub3.images"
                                  }
                                }
                              ]
                            }
                            """),
                        _ => throw new AssertFailedException($"Unexpected request: {uri}")
                    };
                }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Books:Opds:SettingsPath"] = Path.Combine(root, "opds-sources.json")
                    })
                .Build();
            var books = new BookCatalogService(
                client,
                db,
                new NoopBookTranslator(),
                configuration);

            var indexerStore = new IndexerStore(
                new EphemeralDataProtectionProvider(),
                new DirectoryInfo(root));
            var indexers = new IndexerSearchCoordinator(
                new Dictionary<IndexerType, IIndexer>(),
                indexerStore,
                new AcquisitionHealthStore(new DirectoryInfo(root)),
                NullLogger<IndexerSearchCoordinator>.Instance);
            var coordinator = new BookSearchCoordinator(
                books,
                indexers,
                BookProfiles(root),
                NullLogger<BookSearchCoordinator>.Instance);

            var response = await coordinator.SearchAsync(
                "Treasure Island",
                CancellationToken.None);

            var result = Assert.ContainsSingle(response.Items);
            Assert.IsTrue(result.Availability.DirectOrFree);
            Assert.IsTrue(result.Book.CanAcquire);
            CollectionAssert.Contains(
                result.Book.Identities.ToArray(),
                "ol-OLTREASUREW");
            Assert.AreEqual("Project Gutenberg", result.Book.SourceName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static QualityProfileStore BookProfiles(string root) =>
        new(
            new DirectoryInfo(Path.Combine(root, "quality-profiles")),
            new MediaAcquisitionRegistry([
                new BookAcquisitionRegistration()
            ]));

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                System.Text.Encoding.UTF8,
                "application/opds+json")
        };

    private sealed class FakeIndexer : IIndexer
    {
        public int Searches { get; private set; }

        public IndexerType Type => IndexerType.Newznab;

        public Task<IndexerConnectionTestResult> TestAsync(
            IndexerEntry entry,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new IndexerConnectionTestResult(true));

        public Task<IReadOnlyList<AcquisitionCandidate>> SearchAsync(
            IndexerEntry entry,
            IndexerSearchQuery query,
            CancellationToken cancellationToken)
        {
            Searches++;
            IReadOnlyList<AcquisitionCandidate> releases =
            [
                new AcquisitionCandidate(
                    "Treasure Island EPUB",
                    entry.Name,
                    1,
                    "usenet",
                    2_000_000,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    1,
                    1,
                    "treasure-island-epub",
                    null,
                    AnimeReleaseParser.Parse("Treasure Island EPUB"),
                    [],
                    new Uri("https://indexer.example/treasure-island.nzb"),
                    null)
            ];
            return Task.FromResult(releases);
        }
    }

    private sealed class NoopBookTranslator : IBookTranslator
    {
        public string Id => "noop-book-search";

        public Task<string> TranslateLiteraryAsync(
            string sourceText,
            string sourceLanguage,
            string targetLanguage,
            string context,
            CancellationToken cancellationToken) =>
            Task.FromResult(sourceText);
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
