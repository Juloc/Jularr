using Jularr.Web.Data;
using Jularr.Web.Features.Operations;
using Jularr.Web.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Web.Features.Books;

public enum BookTranslationJobState
{
    Idle,
    Queued,
    Running,
    Failed
}

/// <summary>The latest whole-book translation run of one book and language, as the Library page shows it.</summary>
public sealed record BookTranslationJob(BookTranslationJobState State, string? Error, int? ProgressPercent)
{
    public static readonly BookTranslationJob None = new(BookTranslationJobState.Idle, null, null);

    public bool IsActive => State is BookTranslationJobState.Queued or BookTranslationJobState.Running;
}

/// <summary>
/// The one owner of whole-book translation runs: it queues the operation, finds the latest run of a book and language, and
/// guarantees that at most one run per book and language is active. Finished chapters and chunks are cached by
/// <see cref="BookCatalogService"/>, so every new run resumes where the previous one stopped (failure, usage limit, restart).
/// </summary>
public sealed class BookTranslationJobs(AppDbContext db, BackgroundJobQueue queue, BookCatalogService books)
{
    public const string OperationKind = "book-translation";

    public async Task<BookTranslationJob> GetAsync(Guid workId, string language, CancellationToken cancellationToken)
    {
        var runs = await new OperationStore(db).ListAsync(new OperationListFilter(Kind: OperationKind, Limit: 200), cancellationToken);
        var latest = runs.FirstOrDefault(run => string.Equals(run.Details, RunKey(workId, language), StringComparison.Ordinal));
        return latest?.Status switch
        {
            OperationStatus.Queued => new BookTranslationJob(BookTranslationJobState.Queued, null, null),
            OperationStatus.Running => new BookTranslationJob(BookTranslationJobState.Running, null, latest.ProgressPercent),
            OperationStatus.Failed or OperationStatus.Interrupted => new BookTranslationJob(BookTranslationJobState.Failed, latest.Error ?? latest.Message, null),
            _ => BookTranslationJob.None
        };
    }

    /// <summary>
    /// Queues a run that translates every chapter without a cached translation. With <paramref name="discardExisting"/> the
    /// cached translations and chunks of this book and language are deleted first (Redo). Returns false, changing nothing,
    /// while another run of the same book and language is queued or running.
    /// </summary>
    public async Task<bool> QueueAsync(
        Guid workId,
        string title,
        string language,
        string profileId,
        bool discardExisting,
        CancellationToken cancellationToken)
    {
        if ((await GetAsync(workId, language, cancellationToken)).IsActive)
        {
            return false;
        }

        if (discardExisting)
        {
            await books.ClearBookTranslationsAsync(workId, language, cancellationToken);
        }

        var languageName = BookLanguageCatalog.GetName(language);
        await queue.QueueAsync(
            new OperationDescriptor(
                OperationKind,
                "Translation",
                "Translate book",
                title,
                profileId,
                OperationLane.Normal,
                Retryable: true,
                Details: RunKey(workId, language)),
            async (operation, services, workerToken) =>
            {
                await operation.ReportAsync(5, $"Translating book to {languageName}.", cancellationToken: workerToken);
                await services.GetRequiredService<BookCatalogService>().TranslateBookAsync(workId, language, workerToken);
                await operation.ReportAsync(100, "Book translation completed.", cancellationToken: workerToken);
            },
            cancellationToken);
        return true;
    }

    private static string RunKey(Guid workId, string language) => $"{workId:N}:{BookLanguageCatalog.Normalize(language)}";
}
