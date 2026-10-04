using System.IO.Compression;
using System.Net;
using System.Text;
using Jularr.Web.Data;
using Jularr.Web.Features.Books;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Jularr.Tests;

[TestClass]
public sealed class BookCatalogServiceTests
{
    [TestMethod]
    public void ReadableSampleStripsGutenbergBoilerplateAndSkipsDenseContents()
    {
        var raw = """
            Project Gutenberg header
            *** START OF THE PROJECT GUTENBERG EBOOK TEST ***

            CONTENTS

            CHAPTER I. One
            CHAPTER II. Two
            CHAPTER III. Three

            CHAPTER I. One

            This is the real first chapter. It has enough prose to be readable.
            Another paragraph continues the story and establishes the actual content.
            """ + new string('x', 1200) + """

            CHAPTER II. Two

            Later chapter.
            *** END OF THE PROJECT GUTENBERG EBOOK TEST ***
            trailing license
            """;

        var sample = BookCatalogService.ExtractReadableSample(raw, 1000);

        Assert.IsTrue(sample.StartsWith("CHAPTER I. One", StringComparison.Ordinal));
        Assert.IsTrue(sample.Contains("real first chapter", StringComparison.Ordinal));
        Assert.IsFalse(sample.Contains("Project Gutenberg header", StringComparison.Ordinal));
        Assert.IsFalse(sample.Contains("trailing license", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SearchUsesOpenLibraryForGeneralTitles()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                return request.RequestUri?.Host switch
                {
                    "openlibrary.org" => JsonResponse("""
                        {
                          "docs": [
                            {
                              "key": "/works/OL27448W",
                              "title": "The Lord of the Rings",
                              "author_name": ["J. R. R. Tolkien"],
                              "cover_i": 14625765,
                              "first_publish_year": 1954,
                              "subject": ["Fantasy fiction", "Middle Earth"]
                            }
                          ]
                        }
                        """),
                    "www.googleapis.com" => JsonResponse("""
                        {
                          "items": [
                            {
                              "id": "google-lotr",
                              "volumeInfo": {
                                "title": "The Lord of the Rings",
                                "authors": ["J. R. R. Tolkien"],
                                "description": "<p>Epic high fantasy in Middle-earth.</p>",
                                "categories": ["Fantasy"],
                                "publishedDate": "1954",
                                "imageLinks": {
                                  "thumbnail": "http://books.google.com/cover.jpg"
                                }
                              }
                            }
                          ]
                        }
                        """),
                    "id.wikisource.org" => JsonResponse("""
                        {
                          "query": {
                            "search": []
                          }
                        }
                        """),
                    _ => throw new AssertFailedException(
                        $"Unexpected request: {request.RequestUri}")
                };
            }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var service = NewService(db, client);
            var books = await service.SearchAsync(
                "Herr der Ringe",
                CancellationToken.None);

            Assert.AreEqual(1, books.Count);
            Assert.AreEqual("ol-OL27448W", books[0].Id);
            Assert.AreEqual("The Lord of the Rings", books[0].Title);
            Assert.AreEqual("J. R. R. Tolkien", books[0].Author);
            Assert.AreEqual(1954, books[0].FirstPublishYear);
            Assert.AreEqual(
                "Epic high fantasy in Middle-earth.",
                books[0].Summary);
            Assert.IsTrue(
                books[0].Subjects.Contains(
                    "Fantasy",
                    StringComparer.OrdinalIgnoreCase));
            Assert.AreEqual(
                "https://books.google.com/cover.jpg",
                books[0].CoverImageUrl);
            Assert.IsFalse(books[0].CanAcquire);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void BookLanguageCatalogAcceptsFlexibleTagsAndModernIndonesian()
    {
        Assert.AreEqual(
            "id-modern",
            BookLanguageCatalog.Normalize("ID_MODERN"));
        Assert.AreEqual(
            "Modern Indonesian",
            BookLanguageCatalog.GetName("id-modern"));
        Assert.AreEqual(
            "sv-se",
            BookLanguageCatalog.Normalize("sv-SE"));
        Assert.AreEqual(
            "id",
            BookLanguageCatalog.Normalize("not a language tag"));
    }

    [TestMethod]
    public async Task IndonesianWikisourceCanBeSearchedImportedAndReadLocally()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);

            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                var uri = request.RequestUri
                    ?? throw new AssertFailedException("Request URI was missing.");

                if (uri.Host == "openlibrary.org")
                {
                    return JsonResponse("""{"docs":[]}""");
                }

                if (uri.Host == "www.googleapis.com")
                {
                    return JsonResponse("""{"items":[]}""");
                }

                if (uri.Host != "id.wikisource.org")
                {
                    throw new AssertFailedException(
                        $"Unexpected request: {uri}");
                }

                var query = Uri.UnescapeDataString(uri.Query);

                if (query.Contains(
                    "list=search",
                    StringComparison.Ordinal))
                {
                    return JsonResponse("""
                        {
                          "query": {
                            "search": [
                              {
                                "pageid": 42,
                                "title": "Sitti Nurbaya",
                                "snippet": "<span>Novel Indonesia klasik</span>"
                              }
                            ]
                          }
                        }
                        """);
                }

                if (query.Contains(
                    "pageids=42",
                    StringComparison.Ordinal))
                {
                    return JsonResponse("""
                        {
                          "query": {
                            "pages": [
                              {
                                "pageid": 42,
                                "title": "Sitti Nurbaya",
                                "fullurl": "https://id.wikisource.org/wiki/Sitti_Nurbaya"
                              }
                            ]
                          }
                        }
                        """);
                }

                if (query.Contains(
                    "pageid=42",
                    StringComparison.Ordinal))
                {
                    return JsonResponse("""
                        {
                          "parse": {
                            "title": "Sitti Nurbaya",
                            "displaytitle": "Sitti Nurbaya",
                            "text": "<p>Daftar bab</p>",
                            "links": [
                              {"ns": 0, "title": "Sitti Nurbaya/Bab 1"},
                              {"ns": 0, "title": "Sitti Nurbaya/Bab 2"}
                            ]
                          }
                        }
                        """);
                }

                if (query.Contains(
                    "page=Sitti Nurbaya/Bab 1",
                    StringComparison.Ordinal))
                {
                    return JsonResponse("""
                        {
                          "parse": {
                            "title": "Sitti Nurbaya/Bab 1",
                            "displaytitle": "I. Pulang dari Sekolah",
                            "text": "<p>Kira-kira pukul satu siang, kelihatan dua orang anak muda.</p><p>Sitti Nurbaya pulang dari sekolah.</p>",
                            "links": []
                          }
                        }
                        """);
                }

                if (query.Contains(
                    "page=Sitti Nurbaya/Bab 2",
                    StringComparison.Ordinal))
                {
                    return JsonResponse("""
                        {
                          "parse": {
                            "title": "Sitti Nurbaya/Bab 2",
                            "displaytitle": "II. Sutan Mahmud",
                            "text": "<p>Pada senja hari, Sutan Mahmud pulang ke rumah.</p>",
                            "links": []
                          }
                        }
                        """);
                }

                throw new AssertFailedException(
                    $"Unexpected Wikisource request: {uri}");
            }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var service = NewService(db, client);
            var results = await service.SearchAsync(
                "Sitti Nurbaya",
                CancellationToken.None);

            var source = results.Single(x =>
                x.Id == "wsid-42");
            Assert.IsTrue(source.CanAcquire);
            Assert.AreEqual(
                "Indonesian Wikisource",
                source.SourceName);

            var workId = await service.AcquireCatalogBookAsync(
                source.Id,
                CancellationToken.None);

            var work = await db.NovelWorks
                .AsNoTracking()
                .SingleAsync(x => x.Id == workId);
            Assert.AreEqual(
                "wikisource-id",
                work.MetadataProvider);
            Assert.AreEqual(
                "EPUB:id",
                work.Format);

            var chapters = await db.NovelChapters
                .AsNoTracking()
                .Where(x => x.WorkId == workId)
                .OrderBy(x => x.Number)
                .ToArrayAsync();

            Assert.AreEqual(2, chapters.Length);
            Assert.AreEqual(
                "I. Pulang dari Sekolah",
                chapters[0].Title);
            StringAssert.Contains(
                chapters[0].OriginalText,
                "Sitti Nurbaya pulang dari sekolah.");

            var reader = await service.GetReaderChapterAsync(
                chapters[0].Id,
                "profile-1",
                "id",
                CancellationToken.None);

            Assert.IsNotNull(reader);
            Assert.AreEqual(
                "id",
                reader.SourceLanguage);
            Assert.IsNull(reader.Translation);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void WikisourceHtmlExtractionPreservesParagraphsAndDropsEditMarkup()
    {
        var text = BookCatalogService.ExtractWikisourceText(
            """
            <div><p>Paragraf satu.</p>
            <span class="mw-editsection">sunting</span>
            <p>Paragraf <em>dua</em> &amp; selesai.</p></div>
            """);

        Assert.AreEqual(
            "Paragraf satu.\n\nParagraf dua & selesai.",
            text);
    }

    [TestMethod]
    public async Task EmptySearchReturnsOpenLibraryTrendingWithCurrentGoogleCover()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);

            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                if (request.RequestUri?.Host == "openlibrary.org")
                {
                    StringAssert.Contains(
                        request.RequestUri?.AbsolutePath ?? "",
                        "/trending/daily.json");

                    return JsonResponse("""
                        {
                          "works": [
                            {
                              "key": "/works/OL45804W",
                              "title": "Dune",
                              "author_name": ["Frank Herbert"],
                              "cover_i": 15194431,
                              "first_publish_year": 1965,
                              "subject": ["Science fiction"],
                              "isbn": ["9780593099322"],
                              "publisher": ["Ace"],
                              "publish_date": ["2019"]
                            }
                          ]
                        }
                        """);
                }

                if (request.RequestUri?.Host == "www.googleapis.com")
                {
                    return JsonResponse("""
                        {
                          "items": [
                            {
                              "id": "modern-dune",
                              "volumeInfo": {
                                "title": "Dune",
                                "authors": ["Frank Herbert"],
                                "publishedDate": "2019",
                                "industryIdentifiers": [
                                  {"type": "ISBN_13", "identifier": "9780593099322"}
                                ],
                                "imageLinks": {
                                  "large": "http://books.google.com/modern-dune.jpg"
                                }
                              }
                            }
                          ]
                        }
                        """);
                }

                throw new AssertFailedException(
                    $"Unexpected request: {request.RequestUri}");
            }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var service = NewService(db, client);
            var books = await service.SearchAsync(
                null,
                CancellationToken.None);

            Assert.AreEqual(1, books.Count);
            Assert.AreEqual("ol-OL45804W", books[0].Id);
            Assert.AreEqual("Dune", books[0].Title);
            Assert.AreEqual("Frank Herbert", books[0].Author);
            Assert.AreEqual(
                "https://books.google.com/modern-dune.jpg",
                books[0].CoverImageUrl);
            Assert.IsFalse(books[0].CanAcquire);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task GoogleOnlyBookCanBeOpenedAsMetadataResult()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);

            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                Assert.AreEqual("www.googleapis.com", request.RequestUri?.Host);

                return JsonResponse("""
                    {
                      "id": "abc_DEF-123",
                      "volumeInfo": {
                        "title": "A Metadata Only Book",
                        "authors": ["Example Author"],
                        "description": "Description from Google Books.",
                        "categories": ["History"],
                        "publishedDate": "2019-06-01",
                        "infoLink": "https://books.google.com/books?id=abc_DEF-123"
                      }
                    }
                    """);
            }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var service = NewService(db, client);
            var book = await service.GetAsync(
                "gb-abc_DEF-123",
                CancellationToken.None);

            Assert.IsNotNull(book);
            Assert.AreEqual(
                "A Metadata Only Book",
                book.Title);
            Assert.AreEqual(
                "Google Books",
                book.SourceName);
            Assert.AreEqual(
                2019,
                book.FirstPublishYear);
            Assert.IsFalse(book.CanAcquire);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ReadableSampleFollowsGutenbergRedirect()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var requested = new List<string>();

            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                var uri = request.RequestUri
                    ?? throw new AssertFailedException("Request URI was missing.");
                requested.Add(uri.ToString());

                if (uri.AbsolutePath == "/ebooks/2701.txt.utf-8")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri(
                        "/cache/epub/2701/pg2701.txt",
                        UriKind.Relative);
                    return redirect;
                }

                if (uri.AbsolutePath == "/cache/epub/2701/pg2701.txt")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "*** START OF TEST ***\nCall me Ishmael.\n*** END OF TEST ***",
                            Encoding.UTF8,
                            "text/plain")
                    };
                }

                throw new AssertFailedException($"Unexpected request: {uri}");
            }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var service = NewService(db, client);
            var book = new BookCatalogItem(
                "2701",
                "Moby Dick",
                "Herman Melville",
                null,
                null,
                [],
                1851,
                "https://www.gutenberg.org/ebooks/2701.txt.utf-8",
                null,
                "https://www.gutenberg.org/ebooks/2701",
                "Project Gutenberg",
                "Project Gutenberg");

            var sample = await service.GetReadableSampleAsync(
                book,
                CancellationToken.None,
                500);

            Assert.AreEqual("Call me Ishmael.", sample);
            CollectionAssert.AreEqual(
                new[]
                {
                    "https://www.gutenberg.org/ebooks/2701.txt.utf-8",
                    "https://www.gutenberg.org/cache/epub/2701/pg2701.txt"
                },
                requested);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void EpubParserUsesPackageSpineAndPreservesParagraphs()
    {
        using var epub = BuildTestEpub();

        var book = EpubBookParser.Parse(
            epub,
            "fallback.epub");

        Assert.AreEqual("Test Book", book.Title);
        Assert.AreEqual("Test Author", book.Author);
        Assert.AreEqual("en", book.Language);
        Assert.AreEqual("9780306406157", book.Isbn13);
        Assert.AreEqual("Test Publisher", book.Publisher);
        Assert.AreEqual("2026-09-25", book.PublishedDate);
        Assert.AreEqual(2, book.Chapters.Count);
        Assert.AreEqual("Chapter One", book.Chapters[0].Title);
        StringAssert.Contains(book.Chapters[0].Text, "Hello world.");
        StringAssert.Contains(book.Chapters[0].Text, "Next paragraph.");
        CollectionAssert.Contains(book.Subjects.ToArray(), "Fantasy");
    }

    [TestMethod]
    public async Task EpubImportIsDeterministicAndCreatesCanonicalReadingChapters()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            using var client = new HttpClient(new DelegateHttpMessageHandler(
                request => OfflineMetadata(request, "Import should not use HTTP.")))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var service = NewService(db, client);

            await using var first = BuildTestEpub();
            var firstId = await service.ImportUploadedEpubAsync(
                first,
                "test.epub",
                CancellationToken.None);

            await using var second = BuildTestEpub();
            var secondId = await service.ImportUploadedEpubAsync(
                second,
                "test.epub",
                CancellationToken.None);

            Assert.AreEqual(firstId, secondId);
            Assert.AreEqual(1, await db.NovelWorks.CountAsync());
            Assert.AreEqual(2, await db.NovelChapters.CountAsync());

            var work = await db.NovelWorks.SingleAsync();
            Assert.AreEqual(BookCatalogService.ImportedBookProvider, work.SourceProvider);
            Assert.AreEqual("Test Book", work.Title);
            Assert.AreEqual("Test Author", work.Author);
            Assert.AreEqual("EPUB:en", work.Format);

            var edition = await db.BookEditions.SingleAsync();
            Assert.AreEqual(work.Id, edition.WorkId);
            Assert.AreEqual("en", edition.Language);
            Assert.AreEqual("9780306406157", edition.Isbn13);
            Assert.AreEqual("Test Publisher", edition.Publisher);
            Assert.AreEqual("2026-09-25", edition.PublishedDate);
            Assert.IsTrue(edition.IsPrimary);

            var file = await db.BookFiles.SingleAsync();
            Assert.AreEqual(edition.Id, file.EditionId);
            Assert.AreEqual("test.epub", file.FileName);
            Assert.AreEqual("EPUB", file.Format);
            Assert.AreEqual("upload", file.SourceKind);
            Assert.IsTrue(file.ContentHash.Length == 64);
            Assert.IsTrue(file.SizeBytes > 0);
            Assert.IsTrue(file.IsPrimary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task UploadedEpubCachesCurrentGoogleEditionCoverLocally()
    {
        var path = TempDatabasePath();
        var covers = Path.Combine(
            Path.GetTempPath(),
            "jularr-book-covers-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                var uri = request.RequestUri
                    ?? throw new AssertFailedException("Missing request URI.");

                if (uri.Host == "www.googleapis.com")
                {
                    StringAssert.Contains(
                        Uri.UnescapeDataString(uri.Query),
                        "isbn:9780306406157");

                    return JsonResponse("""
                        {
                          "items": [
                            {
                              "id": "current-edition",
                              "volumeInfo": {
                                "title": "Test Book",
                                "authors": ["Test Author"],
                                "publisher": "Modern Publisher",
                                "publishedDate": "2026",
                                "industryIdentifiers": [
                                  {"type": "ISBN_13", "identifier": "9780306406157"}
                                ],
                                "imageLinks": {
                                  "extraLarge": "https://books.google.com/current-cover.png"
                                }
                              }
                            }
                          ]
                        }
                        """);
                }

                if (uri.Host == "books.google.com")
                {
                    return ImageResponse(
                        Convert.FromBase64String(
                            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="),
                        "image/png");
                }

                throw new AssertFailedException(
                    $"Unexpected request: {uri}");
            }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var service = NewService(
                db,
                client,
                configuration: new Dictionary<string, string?>
                {
                    ["Books:CoversPath"] = covers
                });

            await using var epub = BuildTestEpub();
            var workId = await service.ImportUploadedEpubAsync(
                epub,
                "test.epub",
                CancellationToken.None);

            var work = await db.NovelWorks
                .AsNoTracking()
                .SingleAsync(x => x.Id == workId);

            Assert.AreEqual(
                $"/Books/Cover/{workId}",
                work.CoverImageUrl);
            var coverPath = await service.GetLocalCoverPathAsync(workId, null, CancellationToken.None);
            Assert.IsNotNull(coverPath);
            Assert.IsTrue(File.Exists(coverPath));
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(covers))
            {
                Directory.Delete(covers, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task EmbeddedEpubCoverOutranksATitleMatchAndAMissingCoverIsMatched()
    {
        var path = TempDatabasePath();
        var covers = Path.Combine(Path.GetTempPath(), "jularr-book-covers-" + Guid.NewGuid().ToString("N"));
        byte[] googleCover = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8, 0xFF, 0xD9];
        var googleSearches = new List<string>();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!;
                if (uri.Host == "www.googleapis.com")
                {
                    googleSearches.Add(Uri.UnescapeDataString(uri.Query));
                    return JsonResponse("""
                        {
                          "items": [
                            { "id": "messiah", "volumeInfo": { "title": "Dune Messiah", "authors": ["Frank Herbert"], "publishedDate": "2024",
                              "imageLinks": { "thumbnail": "http://books.google.com/books/content?id=messiah&zoom=1&edge=curl" } } },
                            { "id": "dune", "volumeInfo": { "title": "Dune", "authors": ["Frank Herbert"], "publishedDate": "2019",
                              "imageLinks": { "thumbnail": "http://books.google.com/books/content?id=dune&zoom=1&edge=curl" } } }
                          ]
                        }
                        """);
                }

                if (uri.Host == "books.google.com")
                {
                    Assert.AreEqual("?id=dune&zoom=1", uri.Query, "The matching work's cover, without the page-curl effect; a sequel is another work.");
                    return ImageResponse(googleCover, "image/jpeg");
                }

                throw new AssertFailedException($"Unexpected request: {uri}");
            }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };
            var service = NewService(db, client, configuration: new Dictionary<string, string?> { ["Books:CoversPath"] = covers });

            await using var withCover = new EpubTestBuilder { Title = "Dune", Author = "Frank Herbert", Language = "en", Identifier = "urn:uuid:dune-own", CoverPath = "cover.png" }
                .Image("cover.png")
                .Chapter("c1.xhtml", "Book One", "A beginning is the time for taking the most delicate care.")
                .Build();
            var ownId = await service.ImportUploadedEpubAsync(withCover, "dune.epub", CancellationToken.None);
            CollectionAssert.AreEqual(EpubTestBuilder.Png, await File.ReadAllBytesAsync((await service.GetLocalCoverPathAsync(ownId, null, CancellationToken.None))!), "The file's own cover is the actual edition.");
            Assert.AreEqual(0, googleSearches.Count, "Without an ISBN nothing outranks the embedded cover, so no lookup is made.");

            await using var withoutCover = new EpubTestBuilder { Title = "Dune", Author = "Frank Herbert", Language = "en", Identifier = "urn:uuid:dune-plain" }
                .Chapter("c1.xhtml", "Book One", "Arrakis, the desert planet.")
                .Build();
            var plainId = await service.ImportUploadedEpubAsync(withoutCover, "dune-plain.epub", CancellationToken.None);
            CollectionAssert.AreEqual(googleCover, await File.ReadAllBytesAsync((await service.GetLocalCoverPathAsync(plainId, null, CancellationToken.None))!), "A book without a cover gets its matched current cover.");
            Assert.AreEqual($"/Books/Cover/{plainId}", (await db.NovelWorks.AsNoTracking().SingleAsync(x => x.Id == plainId)).CoverImageUrl);
            StringAssert.Contains(googleSearches.Single(), "intitle:Dune inauthor:Frank Herbert");
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(covers))
            {
                Directory.Delete(covers, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task BookTranslationIsCachedBySourceHashAndTargetLanguage()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            using var client = new HttpClient(new DelegateHttpMessageHandler(
                request => OfflineMetadata(request, "Translation should not use HTTP.")))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };
            var translator = new FakeBookTranslator();
            var service = NewService(db, client, translator);

            await using var epub = BuildTestEpub();
            var workId = await service.ImportUploadedEpubAsync(
                epub,
                "test.epub",
                CancellationToken.None);

            var chapterId = await db.NovelChapters
                .Where(x => x.WorkId == workId)
                .OrderBy(x => x.Number)
                .Select(x => x.Id)
                .FirstAsync();

            var first = await service.TranslateChapterAsync(
                chapterId,
                "id",
                CancellationToken.None);
            var second = await service.TranslateChapterAsync(
                chapterId,
                "id",
                CancellationToken.None);

            Assert.AreEqual(first.Id, second.Id);
            Assert.AreEqual(1, translator.CallCount);
            Assert.AreEqual("id", first.TargetLanguage);
            StringAssert.StartsWith(first.Text, "[id]");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task LibraryDetailReportsTranslatedChapterCoverageForEveryCachedLanguage()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            using var client = new HttpClient(new DelegateHttpMessageHandler(
                request => OfflineMetadata(request, "Coverage should not use HTTP.")))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };
            var service = NewService(db, client, new FakeBookTranslator());

            await using var epub = BuildTestEpub();
            var workId = await service.ImportUploadedEpubAsync(epub, "test.epub", CancellationToken.None);
            var chapterId = await db.NovelChapters.Where(x => x.WorkId == workId).OrderBy(x => x.Number).Select(x => x.Id).FirstAsync();
            await service.TranslateChapterAsync(chapterId, "id", CancellationToken.None);
            await service.TranslateChapterAsync(chapterId, "de", CancellationToken.None);

            var detail = await service.GetLibraryBookAsync(workId, "profile", "en", CancellationToken.None);

            Assert.IsNotNull(detail);
            CollectionAssert.AreEqual(new[] { "de", "id" }, detail.TranslationCoverage.Select(x => x.Language).ToArray());
            Assert.IsTrue(detail.TranslationCoverage.All(x => x.TranslatedChapters == 1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task LaterBookChapterUsesPreviousTargetTranslationAsContinuityContext()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            using var client = new HttpClient(new DelegateHttpMessageHandler(
                request => OfflineMetadata(request, "Translation should not use HTTP.")))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };
            var translator = new FakeBookTranslator();
            var service = NewService(db, client, translator);

            await using var epub = BuildTestEpub();
            var workId = await service.ImportUploadedEpubAsync(
                epub,
                "continuity.epub",
                CancellationToken.None);

            var chapters = await db.NovelChapters
                .Where(x => x.WorkId == workId)
                .OrderBy(x => x.Number)
                .Select(x => x.Id)
                .ToArrayAsync();

            Assert.AreEqual(2, chapters.Length);

            await service.TranslateChapterAsync(
                chapters[0],
                "id",
                CancellationToken.None);
            await service.TranslateChapterAsync(
                chapters[1],
                "id",
                CancellationToken.None);

            Assert.AreEqual(2, translator.CallCount);
            Assert.AreEqual(2, translator.Contexts.Count);

            var secondContext = translator.Contexts[1];
            StringAssert.Contains(
                secondContext,
                "Previous source chapter ending");
            StringAssert.Contains(
                secondContext,
                "Previously established Indonesian translation ending");
            StringAssert.Contains(
                secondContext,
                "Hello world.");
            StringAssert.Contains(
                secondContext,
                "[id]");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task BookManagementClearsTranslationsAndDeleteCascadesCanonicalState()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            using var client = new HttpClient(new DelegateHttpMessageHandler(
                request => OfflineMetadata(request, "Book management should not use HTTP.")))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var translator = new FakeBookTranslator();
            var service = NewService(
                db,
                client,
                translator);

            await using var epub = BuildTestEpub();
            var workId = await service.ImportUploadedEpubAsync(
                epub,
                "managed.epub",
                CancellationToken.None);

            var chapterId = await db.NovelChapters
                .Where(x => x.WorkId == workId)
                .OrderBy(x => x.Number)
                .Select(x => x.Id)
                .FirstAsync();

            await service.TranslateChapterAsync(
                chapterId,
                "id",
                CancellationToken.None);

            await service.SaveProgressAsync(
                "profile-1",
                workId,
                chapterId,
                420,
                "translation",
                CancellationToken.None);

            await service.AddBookmarkAsync(
                "profile-1",
                workId,
                chapterId,
                500,
                "translation",
                CancellationToken.None);

            var removedTranslations =
                await service.ClearBookTranslationsAsync(
                    workId,
                    "id",
                    CancellationToken.None);

            Assert.AreEqual(1, removedTranslations);
            Assert.AreEqual(
                0,
                await db.NovelTranslations.CountAsync());
            Assert.AreEqual(
                1,
                await db.NovelProgress.CountAsync());
            Assert.AreEqual(
                1,
                await db.NovelBookmarks.CountAsync());

            await service.TranslateChapterAsync(
                chapterId,
                "id",
                CancellationToken.None);

            await service.DeleteImportedBookAsync(
                workId,
                CancellationToken.None);

            Assert.AreEqual(
                0,
                await db.NovelWorks.CountAsync());
            Assert.AreEqual(
                0,
                await db.NovelChapters.CountAsync());
            Assert.AreEqual(
                0,
                await db.NovelTranslations.CountAsync());
            Assert.AreEqual(
                0,
                await db.NovelProgress.CountAsync());
            Assert.AreEqual(
                0,
                await db.NovelBookmarks.CountAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task IntegrationSettingsPersistWithoutExposingDefaults()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "jularr-books-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "integrations.json");

        try
        {
            await BookIntegrationSettingsStore.SaveAsync(
                new BookIntegrationSettings("./book-inbox"),
                CancellationToken.None,
                path);

            var loaded = BookIntegrationSettingsStore.Load(path);

            Assert.AreEqual(
                Path.GetFullPath("./book-inbox"),
                loaded.InboxPath);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(
                    directory,
                    recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task HardcoverListStateIsProfileEnrichmentNotASecondCatalog()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var token = "hardcover-test-" + Guid.NewGuid().ToString("N");
            var listRequests = 0;
            using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
            {
                Assert.AreEqual("api.hardcover.app", request.RequestUri?.Host);
                Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
                Assert.AreEqual(token, request.Headers.Authorization?.Parameter, "A pasted \"Bearer \" prefix is not sent twice.");
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Contains("JularrViewer", StringComparison.Ordinal))
                {
                    return JsonResponse("""{ "data": { "me": [ { "id": 4242, "username": "reader" } ] } }""");
                }

                listRequests++;
                StringAssert.Contains(body, "\"userId\":4242", "The shelf is read for the connected user only.");
                return JsonResponse("""
                    {
                      "data": {
                        "user_books": [
                          {
                            "status_id": 2,
                            "book": { "title": "Dune", "cached_contributors": [ { "author": { "name": "Frank Herbert" }, "contribution": null } ] },
                            "edition": { "isbn_13": "9780593099322", "isbn_10": null }
                          },
                          {
                            "status_id": 1,
                            "book": { "title": "Atomic Habits", "cached_contributors": [ { "author": { "name": "James Clear" }, "contribution": null } ] },
                            "edition": null
                          },
                          {
                            "status_id": 6,
                            "book": { "title": "Emma", "cached_contributors": [ { "author": { "name": "Jane Austen" } } ] },
                            "edition": null
                          }
                        ]
                      }
                    }
                    """);
            }))
            {
                BaseAddress = new Uri("https://gutendex.com/")
            };

            var service = NewService(db, client);
            var viewer = await service.ValidateHardcoverTokenAsync("Bearer " + token, CancellationToken.None);
            Assert.AreEqual(new HardcoverViewer(4242, "reader"), viewer);

            var account = new StoredHardcoverAccount(viewer.UserId, viewer.Username, token, DateTimeOffset.UtcNow);
            IReadOnlyList<BookCatalogItem> catalog =
            [
                new BookCatalogItem("ol-OL45804W", "Dune", "Frank Herbert", null, null, [], 1965, null, null,
                    "https://openlibrary.org/works/OL45804W", "Open Library", null)
                {
                    Isbns = ["9780593099322"]
                },
                new BookCatalogItem("gb-ah", "Atomic Habits: An Easy & Proven Way to Build Good Habits", "James Clear", null, null, [], 2018, null, null,
                    "https://books.google.com/ah", "Google Books", null),
                new BookCatalogItem("ol-EMMA", "Emma", "Jane Austen", null, null, [], 1815, null, null,
                    "https://openlibrary.org/works/EMMA", "Open Library", null),
                new BookCatalogItem("ol-DM", "Dune Messiah", "Frank Herbert", null, null, [], 1969, null, null,
                    "https://openlibrary.org/works/DM", "Open Library", null)
            ];

            var result = await service.EnrichHardcoverStatesAsync(catalog, account, CancellationToken.None);
            await service.EnrichHardcoverStatesAsync(catalog, account, CancellationToken.None);

            CollectionAssert.AreEqual(
                new string?[] { BookListStates.Reading, BookListStates.WantToRead, null, null },
                result.Select(item => item.ExternalListState).ToArray(),
                "ISBN or work identity matches; \"Ignored\" is no list state; a similar title is another book.");
            Assert.AreEqual(1, listRequests, "The shelf is cached briefly instead of read on every search.");
            Assert.AreSame(
                catalog,
                await service.EnrichHardcoverStatesAsync(catalog, null, CancellationToken.None),
                "Without a connection results stay untouched.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task HardcoverAccountStoreProtectsTokenAndIsolatesProfiles()
    {
        var root = new DirectoryInfo(Path.Combine(
            Path.GetTempPath(),
            "jularr-hardcover-" + Guid.NewGuid().ToString("N")));
        var keys = Directory.CreateDirectory(Path.Combine(root.FullName, "keys"));
        var integrations = Directory.CreateDirectory(Path.Combine(root.FullName, "integrations"));
        const string token = "hc_pat_private_token_123456789";

        try
        {
            var store = new BookHardcoverAccountStore(
                DataProtectionProvider.Create(keys),
                integrations);

            await store.SaveAsync(
                "profile-a",
                new StoredHardcoverAccount(
                    4242,
                    "reader",
                    token,
                    DateTimeOffset.UtcNow),
                CancellationToken.None);

            var loaded = await store.LoadAsync(
                "profile-a",
                CancellationToken.None);
            var other = await store.LoadAsync(
                "profile-b",
                CancellationToken.None);

            Assert.IsNotNull(loaded);
            Assert.AreEqual("reader", loaded.Username);
            Assert.AreEqual(token, loaded.AccessToken);
            Assert.IsNull(other);

            var persisted = await File.ReadAllTextAsync(
                Path.Combine(
                    integrations.FullName,
                    "hardcover",
                    "accounts",
                    "profile-a.json"));
            Assert.IsFalse(
                persisted.Contains(token, StringComparison.Ordinal));
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
    public void RemoteEpubUrlRejectsUnsafeTargets()
    {
        AssertThrows<InvalidOperationException>(() =>
            BookCatalogService.ValidateExternalEpubUriSyntax(
                new Uri("http://example.com/book.epub")));

        AssertThrows<InvalidOperationException>(() =>
            BookCatalogService.ValidateExternalEpubUriSyntax(
                new Uri("https://127.0.0.1/book.epub")));

        AssertThrows<InvalidOperationException>(() =>
            BookCatalogService.ValidateExternalEpubUriSyntax(
                new Uri("https://[::1]/book.epub")));

        BookCatalogService.ValidateExternalEpubUriSyntax(
            new Uri("https://example.com/book.epub"));
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            Assert.Fail(
                $"Expected {typeof(TException).Name} to be thrown.");
        }
        catch (TException)
        {
        }
    }

    private static BookCatalogService NewService(
        AppDbContext db,
        HttpClient client,
        IBookTranslator? translator = null,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var values = new Dictionary<string, string?>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Books:Translation:MemoryPath"] = Path.Combine(
                Path.GetTempPath(),
                "jularr-book-translation-tests",
                Guid.NewGuid().ToString("N"))
        };

        if (configuration is not null)
        {
            foreach (var pair in configuration)
            {
                values[pair.Key] = pair.Value;
            }
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new BookCatalogService(
            client,
            db,
            translator ?? new FakeBookTranslator(),
            config);
    }

    private static MemoryStream BuildTestEpub()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(
            stream,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            AddEntry(
                archive,
                "META-INF/container.xml",
                """
                <?xml version="1.0" encoding="utf-8"?>
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
                  <rootfiles>
                    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" />
                  </rootfiles>
                </container>
                """);

            AddEntry(
                archive,
                "OEBPS/content.opf",
                """
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://www.idpf.org/2007/opf"
                         xmlns:dc="http://purl.org/dc/elements/1.1/"
                         version="3.0">
                  <metadata>
                    <dc:title>Test Book</dc:title>
                    <dc:creator>Test Author</dc:creator>
                    <dc:language>en</dc:language>
                    <dc:description>A test story.</dc:description>
                    <dc:identifier>urn:isbn:978-0-306-40615-7</dc:identifier>
                    <dc:publisher>Test Publisher</dc:publisher>
                    <dc:date>2026-09-25</dc:date>
                    <dc:subject>Fantasy</dc:subject>
                  </metadata>
                  <manifest>
                    <item id="c1" href="chapter1.xhtml" media-type="application/xhtml+xml" />
                    <item id="c2" href="chapter2.xhtml" media-type="application/xhtml+xml" />
                  </manifest>
                  <spine>
                    <itemref idref="c1" />
                    <itemref idref="c2" />
                  </spine>
                </package>
                """);

            AddEntry(
                archive,
                "OEBPS/chapter1.xhtml",
                """
                <html xmlns="http://www.w3.org/1999/xhtml">
                  <body>
                    <h1>Chapter One</h1>
                    <p>Hello <em>world</em>.</p>
                    <p>Next paragraph.</p>
                  </body>
                </html>
                """);

            AddEntry(
                archive,
                "OEBPS/chapter2.xhtml",
                """
                <html xmlns="http://www.w3.org/1999/xhtml">
                  <body>
                    <h1>Chapter Two</h1>
                    <p>The story continues here.</p>
                  </body>
                </html>
                """);
        }

        stream.Position = 0;
        return stream;
    }

    private static void AddEntry(
        ZipArchive archive,
        string path,
        string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(
            entry.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content.Trim());
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };

    /// <summary>
    /// No network: an imported book without an embedded cover asks Google Books for one
    /// (#371), which fails like an outage; any other request is a test failure.
    /// </summary>
    private static HttpResponseMessage OfflineMetadata(
        HttpRequestMessage request,
        string otherwise) =>
        request.RequestUri?.Host == "www.googleapis.com"
            ? throw new HttpRequestException("Metadata providers are offline in tests.")
            : throw new AssertFailedException(otherwise);

    private static HttpResponseMessage ImageResponse(
        byte[] bytes,
        string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        };
    }

    private static string TempDatabasePath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"jularr-books-{Guid.NewGuid():N}.db");

    private static async Task<AppDbContext> CreateDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;

        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }

    private sealed class FakeBookTranslator : IBookTranslator
    {
        public string Id => "fake-books";
        public int CallCount { get; private set; }
        public List<string> Contexts { get; } = [];

        public Task<string> TranslateLiteraryAsync(
            string sourceText,
            string sourceLanguage,
            string targetLanguage,
            string context,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Contexts.Add(context);
            return Task.FromResult(
                $"[{targetLanguage}] {sourceText}");
        }
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
