using System.Security.Claims;
using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Novels;
using Jularr.Web.Infrastructure;
using Jularr.Web.Pages.Books;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>
/// #833: Book/PDF translations are shared work content. Learning configuration may personalize
/// study assistance, but it must neither hide cached translated editions nor authorize translation generation.
/// </summary>
[TestClass]
public sealed class BooksLearningGatingTests
{
    private const string Profile = "books-reader-a";
    private const string OtherProfile = "books-reader-b";

    [TestMethod]
    public async Task TranslationHandlersWorkWithoutLearningConfiguration()
    {
        await using var fixture = await Fixture.CreateAsync();

        var postResult = await fixture.CreateReadModel(Profile).OnPostTranslateAsync(fixture.ChapterId, "de", CancellationToken.None);
        var postJson = JsonDocument.Parse(JsonSerializer.Serialize(((JsonResult)postResult).Value));
        Assert.AreEqual("queued", postJson.RootElement.GetProperty("status").GetString());

        var statusResult = await fixture.CreateReadModel(Profile).OnGetTranslationStatusAsync(fixture.ChapterId, "de", CancellationToken.None);
        var statusJson = JsonDocument.Parse(JsonSerializer.Serialize(((JsonResult)statusResult).Value));
        Assert.AreEqual("pending", statusJson.RootElement.GetProperty("status").GetString());

        var libraryResult = await fixture.CreateLibraryModel(Profile).OnPostTranslateBookAsync(fixture.WorkId, "de", CancellationToken.None);
        Assert.IsInstanceOfType(libraryResult, typeof(RedirectToPageResult));

        var regenerateResult = await fixture.CreateLibraryModel(Fixture.Owner, owner: true).OnPostRegenerateAsync(fixture.WorkId, "fr", CancellationToken.None);
        Assert.IsInstanceOfType(regenerateResult, typeof(RedirectToPageResult));
    }

    [TestMethod]
    public async Task CachedTranslationIsSharedAcrossProfiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedCachedTranslationAsync("de", "Hallo Welt.");

        var first = fixture.CreateReadModel(Profile);
        await first.OnGetAsync(fixture.ChapterId, "de", null, null, null, CancellationToken.None);

        var second = fixture.CreateReadModel(OtherProfile);
        await second.OnGetAsync(fixture.ChapterId, "de", null, null, null, CancellationToken.None);

        Assert.IsNotNull(first.Reader.Translation);
        Assert.IsNotNull(second.Reader.Translation);
        CollectionAssert.AreEqual(first.Reader.TranslatedParagraphs.ToArray(), second.Reader.TranslatedParagraphs.ToArray());
        Assert.AreEqual("Hallo Welt.", second.Reader.TranslatedParagraphs.Single());
    }

    [TestMethod]
    public async Task ReaderKeepsWorkLanguageAffordanceWhenCurrentChapterIsNotTranslated()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedCachedTranslationForOtherChapterAsync("de", "Bereits übersetzt.");

        var reader = fixture.CreateReadModel(OtherProfile);
        await reader.OnGetAsync(fixture.ChapterId, "de", null, null, null, CancellationToken.None);

        Assert.IsNull(reader.Reader.Translation, "The currently open chapter is intentionally untranslated.");
        Assert.IsTrue(reader.HasWorkTranslationLanguage, "A work-level translated edition must stay visible while translation is partial.");
    }

    [TestMethod]
    public void BooksTranslationPathsDoNotUseLearningAsAnAuthorizationGate()
    {
        var root = RepositoryRoot();
        var readModel = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Books", "Read.cshtml.cs"));
        var libraryModel = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Books", "Library.cshtml.cs"));
        var readView = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Books", "Read.cshtml"));
        var libraryView = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Books", "Library.cshtml"));

        foreach (var source in new[] { readModel, libraryModel })
        {
            Assert.IsFalse(source.Contains("LearningModuleResolver", StringComparison.Ordinal));
            Assert.IsFalse(source.Contains("LearningMediaType.Book", StringComparison.Ordinal));
            Assert.IsFalse(source.Contains("TranslationEnabled", StringComparison.Ordinal));
        }

        Assert.IsFalse(readView.Contains("Model.TranslationEnabled", StringComparison.Ordinal));
        Assert.IsFalse(libraryView.Contains("Model.TranslationEnabled", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LibraryViewShowsTranslationStateFromTheSharedCache()
    {
        var view = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages", "Books", "Library.cshtml"));

        StringAssert.Contains(view, "else if (chapter.HasTranslation)");
        Assert.IsFalse(view.Contains("Model.TranslationEnabled && chapter.HasTranslation", StringComparison.Ordinal));
        Assert.IsFalse(view.Contains("Model.SourceIsTarget || Model.TranslationEnabled", StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Owner = "books-learner-owner";

        private readonly string directory;
        private readonly ServiceProvider services;

        private Fixture(string directory, ServiceProvider services, AppDbContext db, Guid workId, Guid chapterId)
        {
            this.directory = directory;
            this.services = services;
            Db = db;
            WorkId = workId;
            ChapterId = chapterId;
        }

        public AppDbContext Db { get; }
        public Guid WorkId { get; }
        public Guid ChapterId { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"jularr-books-translation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var connectionString = $"Data Source={Path.Combine(directory, "jularr.db")};Foreign Keys=True";

            var collection = new ServiceCollection();
            collection.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
            var services = collection.BuildServiceProvider();

            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);

            var work = new NovelWork
            {
                SourceProvider = BookCatalogService.ImportedBookProvider,
                SourceKey = Guid.NewGuid().ToString("N"),
                SourceUrl = "upload://books-gating-test.epub",
                Title = "Gating Test Book",
                Format = "EPUB:en"
            };
            var volume = new NovelVolume
            {
                WorkId = work.Id,
                Number = 1,
                Kind = NovelVolumeKinds.Book,
                SourceKey = "book"
            };
            var chapter = new NovelChapter
            {
                WorkId = work.Id,
                VolumeId = volume.Id,
                Number = 1,
                Title = "Chapter One",
                SourceUrl = "book://books-gating-test/1",
                OriginalText = "Hello world.",
                SourceHash = "hash-1"
            };

            db.AddRange(work, volume, chapter);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            return new Fixture(directory, services, db, work.Id, chapter.Id);
        }

        /// <summary>
        /// Seeds a cached translation directly, matching the cache identity
        /// <see cref="BookCatalogService.GetCachedTranslationAsync(Guid, string, CancellationToken)"/>
        /// looks up (the default <see cref="IBookTranslator.GetTranslationModeAsync"/>
        /// is <c>Efficient</c>), so the reader has content to read while off (#369).
        /// </summary>
        public async Task SeedCachedTranslationAsync(string targetLanguage, string text)
        {
            var chapter = await Db.NovelChapters.SingleAsync(x => x.Id == ChapterId);
            Db.NovelTranslations.Add(new NovelTranslation
            {
                ChapterId = ChapterId,
                TargetLanguage = BookLanguageCatalog.Normalize(targetLanguage),
                ProviderId = $"book-v{BookCatalogService.TranslationPromptVersion}-efficient",
                PromptVersion = BookCatalogService.TranslationPromptVersion,
                SourceHash = chapter.SourceHash,
                Text = text
            });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task SeedCachedTranslationForOtherChapterAsync(string targetLanguage, string text)
        {
            var chapter = new NovelChapter
            {
                WorkId = WorkId,
                VolumeId = await Db.NovelVolumes
                    .Where(x => x.WorkId == WorkId)
                    .Select(x => x.Id)
                    .SingleAsync(),
                Number = 2,
                Title = "Chapter Two",
                SourceUrl = "book://books-gating-test/2",
                OriginalText = "Second chapter.",
                SourceHash = "hash-2"
            };
            Db.NovelChapters.Add(chapter);
            Db.NovelTranslations.Add(new NovelTranslation
            {
                ChapterId = chapter.Id,
                TargetLanguage = BookLanguageCatalog.Normalize(targetLanguage),
                ProviderId = $"book-v{BookCatalogService.TranslationPromptVersion}-efficient",
                PromptVersion = BookCatalogService.TranslationPromptVersion,
                SourceHash = chapter.SourceHash,
                Text = text
            });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public ReadModel CreateReadModel(string profileId) =>
            AttachPageContext(new ReadModel(
                NewBookCatalogService(),
                TestAccounts.Context(profileId),
                new BackgroundJobQueue(services.GetRequiredService<IServiceScopeFactory>()),
                Db));

        public LibraryModel CreateLibraryModel(string profileId, bool owner = false)
        {
            var queue = new BackgroundJobQueue(services.GetRequiredService<IServiceScopeFactory>());
            return AttachPageContext(new LibraryModel(
                Db,
                NewBookCatalogService(),
                owner ? OwnerContext(profileId) : TestAccounts.Context(profileId),
                queue,
                new BookTranslationJobs(Db, queue, NewBookCatalogService()),
                NullLogger<LibraryModel>.Instance));
        }

        /// <summary>
        /// The Ui bundle (localization PR #362) is resolved from
        /// <see cref="PageModel.HttpContext"/>, so page models built for a
        /// direct handler call (not through the MVC pipeline) need a bare
        /// PageContext for that property to be non-null.
        /// </summary>
        private static TPage AttachPageContext<TPage>(TPage page)
            where TPage : PageModel
        {
            page.PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection()
                        .AddSingleton<IModelMetadataProvider, EmptyModelMetadataProvider>()
                        .BuildServiceProvider()
                },
                ViewData = new ViewDataDictionary<TPage>(
                    new EmptyModelMetadataProvider(),
                    new ModelStateDictionary())
            };
            return page;
        }

        private static CurrentAccountContext OwnerContext(string profileId)
        {
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, profileId),
                new Claim(ClaimTypes.Role, AccountRoles.Owner)
            ],
            "test");

            return new CurrentAccountContext(new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(identity)
                }
            });
        }

        private BookCatalogService NewBookCatalogService()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Books:Translation:MemoryPath"] = Path.Combine(directory, "translation-memory")
                })
                .Build();

            return new BookCatalogService(new HttpClient(), Db, new UnusedTranslator(), config);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await services.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private sealed class UnusedTranslator : IBookTranslator
        {
            public string Id => "unused";

            public Task<string> TranslateLiteraryAsync(
                string sourceText,
                string sourceLanguage,
                string targetLanguage,
                string context,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("AI translation is not expected in these ownership tests.");
        }
    }
}
