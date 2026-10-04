using System.Security.Claims;
using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Learning;
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
/// #230/#369: the Books reader's whole-chapter AI translation *generation*
/// (the DE/EN "Translate this chapter" flow, plus the library's whole-book
/// Translate/Regenerate actions) must resolve the Translation capability
/// through the canonical hierarchy. Reading an already cached translated
/// chapter, however, is core reader behaviour and must always work
/// regardless of that capability (#369 fixed a regression where #230
/// withheld cached text while Learning was off). Follows the pattern in
/// NovelLearningTests/HomePageLearningGatingTests.
/// </summary>
[TestClass]
public sealed class BooksLearningGatingTests
{
    private const string Profile = "books-learner";

    [TestMethod]
    public async Task OffResolvesTranslationDisabledAndRefusesTheHandlers()
    {
        await using var fixture = await Fixture.CreateAsync();

        var reader = fixture.CreateReadModel(Profile);
        await reader.OnGetAsync(fixture.ChapterId, "de", null, null, null, CancellationToken.None);
        Assert.IsFalse(reader.TranslationEnabled, "Off must not offer *generating* a chapter translation.");

        var postResult = await fixture.CreateReadModel(Profile).OnPostTranslateAsync(
            fixture.ChapterId,
            "de",
            CancellationToken.None);
        Assert.IsInstanceOfType(postResult, typeof(ForbidResult));

        // No cached translation exists yet, so the status handler implies
        // generation and stays gated (see OffStillExposesACachedTranslation
        // below for the cached-content case, #369).
        var statusResult = await fixture.CreateReadModel(Profile).OnGetTranslationStatusAsync(
            fixture.ChapterId,
            "de",
            CancellationToken.None);
        Assert.IsInstanceOfType(statusResult, typeof(ForbidResult));
    }

    [TestMethod]
    public async Task OffStillExposesACachedTranslationAndTheStatusHandlerReturnsReady()
    {
        // #369: reading an already cached translated chapter is core reader
        // behaviour and must not depend on the Learning Translation
        // capability. Only *generating* a new translation stays gated.
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedCachedTranslationAsync("de", "Hallo Welt.");

        var reader = fixture.CreateReadModel(Profile);
        await reader.OnGetAsync(fixture.ChapterId, "de", null, null, null, CancellationToken.None);

        Assert.IsFalse(reader.TranslationEnabled);
        Assert.IsNotNull(reader.Reader.Translation, "The cached translation must still be loaded.");
        Assert.AreEqual(1, reader.Reader.TranslatedParagraphs.Count);

        var statusResult = await fixture.CreateReadModel(Profile).OnGetTranslationStatusAsync(
            fixture.ChapterId,
            "de",
            CancellationToken.None);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(((JsonResult)statusResult).Value));
        Assert.AreEqual("ready", json.RootElement.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task ReaderKeepsLanguageAffordanceWhenOnlyAnotherChapterIsTranslated()
    {
        // A whole-book translation can be partially complete (for example
        // 20/50 chapters/pages). Opening one of the unfinished chapters must
        // not make the Reader's language control disappear.
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedCachedTranslationForOtherChapterAsync("de", "Bereits übersetzt.");

        var reader = fixture.CreateReadModel(Profile);
        await reader.OnGetAsync(fixture.ChapterId, "de", null, null, null, CancellationToken.None);

        Assert.IsFalse(reader.TranslationEnabled);
        Assert.IsNull(reader.Reader.Translation, "The currently open chapter is intentionally untranslated.");
        Assert.IsTrue(
            reader.HasWorkTranslationLanguage,
            "A cached target-language translation elsewhere in the book must keep the language affordance visible.");

        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Jularr.Web", "Pages", "Books", "Read.cshtml"));
        StringAssert.Contains(view, "Model.HasWorkTranslationLanguage");
        StringAssert.Contains(view, "disabled=\"@(!isPdf && !hasAlternate)\"");
    }

    [TestMethod]
    public async Task LanguageToolsAndStudyEnableTranslationByDefault()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.SetModeAsync(Profile, LearningMode.LanguageTools);
        var languageTools = fixture.CreateReadModel(Profile);
        await languageTools.OnGetAsync(fixture.ChapterId, "de", null, null, null, CancellationToken.None);
        Assert.IsTrue(languageTools.TranslationEnabled, "Language Tools offers translation by default.");

        await fixture.SetModeAsync(Profile, LearningMode.Study);
        var study = fixture.CreateReadModel(Profile);
        await study.OnGetAsync(fixture.ChapterId, "de", null, null, null, CancellationToken.None);
        Assert.IsTrue(study.TranslationEnabled, "Study offers translation by default.");
    }

    [TestMethod]
    public async Task CustomRespectsTheTranslationCapability()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetModeAsync(Profile, LearningMode.Custom);

        var withoutOverride = fixture.CreateReadModel(Profile);
        await withoutOverride.OnGetAsync(fixture.ChapterId, "de", null, null, null, CancellationToken.None);
        Assert.IsFalse(
            withoutOverride.TranslationEnabled,
            "Custom starts with every capability off until the profile opts in.");

        await fixture.SetCapabilityAsync(Profile, LearningCapability.Translation, true);
        var withOverride = fixture.CreateReadModel(Profile);
        await withOverride.OnGetAsync(fixture.ChapterId, "de", null, null, null, CancellationToken.None);
        Assert.IsTrue(withOverride.TranslationEnabled);
    }

    [TestMethod]
    public async Task WorkLevelOverrideEnablesTranslationOnlyForThatBook()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var otherFixture = await Fixture.CreateAsync();

        // Global Off, but this one book is switched to Study.
        await new LearningConfigurationStore(fixture.Db).SetModeAsync(
            Profile,
            LearningScopeRef.ForWork(LearningMediaType.Book, fixture.WorkId.ToString()),
            LearningMode.Study,
            CancellationToken.None);

        var overridden = fixture.CreateReadModel(Profile);
        await overridden.OnGetAsync(fixture.ChapterId, "de", null, null, null, CancellationToken.None);
        Assert.IsTrue(overridden.TranslationEnabled, "The overridden work must resolve Study.");

        var unrelated = otherFixture.CreateReadModel(Profile);
        await unrelated.OnGetAsync(otherFixture.ChapterId, "de", null, null, null, CancellationToken.None);
        Assert.IsFalse(
            unrelated.TranslationEnabled,
            "A different book on the same (globally Off) profile must not inherit the override.");
    }

    // ---- Books/Library. #369: the whole-book Translate/Regenerate actions
    // (generation) still resolve the Translation capability, the same way the
    // reader's own translate handler does. The per-chapter "Translated" badge
    // and the translated-count summary, however, only reflect the cache: they
    // are core reader status, not a Learning affordance.

    [TestMethod]
    public async Task LibraryPageResolvesTranslationDisabledOffAndRefusesTheWholeBookHandlers()
    {
        await using var fixture = await Fixture.CreateAsync();

        var library = fixture.CreateLibraryModel(Profile);
        await library.OnGetAsync(fixture.WorkId, "de", CancellationToken.None);
        Assert.IsFalse(library.TranslationEnabled, "Off must not offer whole-book translation.");

        var translateResult = await fixture.CreateLibraryModel(Profile).OnPostTranslateBookAsync(
            fixture.WorkId,
            "de",
            CancellationToken.None);
        Assert.IsInstanceOfType(translateResult, typeof(ForbidResult));

        var regenerateResult = await fixture.CreateLibraryModel(Fixture.Owner, owner: true).OnPostRegenerateAsync(
            fixture.WorkId,
            "de",
            CancellationToken.None);
        Assert.IsInstanceOfType(regenerateResult, typeof(ForbidResult));
    }

    [TestMethod]
    public async Task LibraryPageEnablesTranslationByDefaultInStudy()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetModeAsync(Profile, LearningMode.Study);

        var library = fixture.CreateLibraryModel(Profile);
        await library.OnGetAsync(fixture.WorkId, "de", CancellationToken.None);
        Assert.IsTrue(library.TranslationEnabled);
    }

    [TestMethod]
    public void LibraryViewGatesOnlyTheGenerationActionsOnTranslationEnabled()
    {
        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Jularr.Web", "Pages", "Books", "Library.cshtml"));

        // Regenerating a whole-book translation must still gate on the resolved
        // capability; generating one is offered by the shared language/edition
        // selector, which only receives the action when TranslationEnabled.
        StringAssert.Contains(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Jularr.Web", "Pages", "Books", "Library.cshtml.cs")), "TranslationEnabled,");
        AssertGuardPrecedesHandler(view, "asp-page-handler=\"Regenerate\"");
    }

    [TestMethod]
    public void LibraryViewShowsTheBadgeAndSummaryFromTheCacheAlone()
    {
        // #369: the per-chapter "Translated" badge and the translated-count
        // summary must reflect chapter.HasTranslation (the cache) regardless
        // of the resolved Learning capability, unlike the generation actions
        // asserted in LibraryViewGatesOnlyTheGenerationActionsOnTranslationEnabled.
        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Jularr.Web", "Pages", "Books", "Library.cshtml"));

        StringAssert.Contains(view, "else if (chapter.HasTranslation)");
        Assert.IsFalse(
            view.Contains("Model.TranslationEnabled && chapter.HasTranslation", StringComparison.Ordinal),
            "The per-chapter badge must not depend on the resolved Learning capability.");
        Assert.IsFalse(
            view.Contains("Model.SourceIsTarget || Model.TranslationEnabled", StringComparison.Ordinal),
            "The translated-count summary must not depend on the resolved Learning capability.");
    }

    /// <summary>Asserts the translation forms only render after the "!Model.TranslationEnabled" branch, so they never show while the capability is off.</summary>
    private static void AssertGuardPrecedesHandler(string view, string handlerMarker)
    {
        var handlerIndex = view.IndexOf(handlerMarker, StringComparison.Ordinal);
        Assert.IsTrue(handlerIndex > 0, $"'{handlerMarker}' was not found.");

        var guardIndex = view.LastIndexOf("@if (!Model.TranslationEnabled)", handlerIndex, StringComparison.Ordinal);
        Assert.IsTrue(guardIndex >= 0, $"No TranslationEnabled guard precedes '{handlerMarker}'.");
        var elseIndex = view.IndexOf("else if (!job.IsActive)", guardIndex, StringComparison.Ordinal);
        Assert.IsTrue(elseIndex > guardIndex && elseIndex < handlerIndex, "The forms belong to the capability-enabled branch.");
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
            var directory = Path.Combine(Path.GetTempPath(), $"jularr-books-gating-{Guid.NewGuid():N}");
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

        public Task SetModeAsync(string profileId, LearningMode mode) =>
            new LearningConfigurationStore(Db).SetModeAsync(
                profileId,
                LearningScopeRef.Profile,
                mode,
                CancellationToken.None);

        public Task SetCapabilityAsync(string profileId, LearningCapability capability, bool enabled) =>
            new LearningConfigurationStore(Db).SetCapabilityOverrideAsync(
                profileId,
                LearningScopeRef.Profile,
                capability,
                enabled,
                CancellationToken.None);

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
                throw new InvalidOperationException("AI translation is not expected in these gating tests.");
        }
    }
}
