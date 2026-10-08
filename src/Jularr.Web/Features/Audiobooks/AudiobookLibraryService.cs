using Jularr.Web.Data;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.MediaCore;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Audiobooks;

/// <summary>One audio file to record on an audiobook: its final name, format and location on disk.</summary>
public sealed record AudiobookFileInput(
    string FileName,
    string Format,
    string StoragePath,
    long SizeBytes,
    long? DurationMs = null);

/// <summary>The audiobook a completed download resolved to and the universal work it is bridged to.</summary>
public sealed record AudiobookLibraryEntry(Audiobook Audiobook, Guid WorkId);

/// <summary>
/// Creates and resolves first-class <see cref="Audiobook"/> records (with their <see cref="AudiobookFile"/>
/// rows) and bridges each to the universal media core (#440). Idempotent on the audiobook's
/// <see cref="Audiobook.Key"/> (folded title + year) so a re-import of the same audiobook refreshes the row
/// and always resolves to the same <see cref="Work"/> through the <c>WorkSourceKind.Audiobook</c> source
/// link; each file is idempotent per audiobook on its folded name.
/// </summary>
public sealed class AudiobookLibraryService(
    AppDbContext db,
    LegacyWorkBridge bridge,
    IInstanceModuleService? instanceModules = null)
{
    public async Task<AudiobookLibraryEntry> EnsureAsync(
        string title,
        int? year,
        string? author,
        string? narrator,
        string? asin,
        long? durationMs,
        int? chapterCount,
        string? libraryPath,
        IReadOnlyList<AudiobookFileInput> files,
        CancellationToken cancellationToken,
        Guid? requestedWorkId = null)
    {
        if (instanceModules is not null
            && !await instanceModules.IsEnabledAsync(
                InstanceModule.Audiobook,
                cancellationToken))
        {
            throw new InvalidOperationException("Audiobook module is disabled.");
        }

        var cleanTitle = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        var key = AudiobookKey(cleanTitle, year);

        var audiobook = await db.Set<Audiobook>().FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (audiobook is null)
        {
            audiobook = new Audiobook
            {
                Key = key,
                Title = cleanTitle,
                Year = year,
                Author = Clean(author),
                Narrator = Clean(narrator),
                Asin = Clean(asin),
                DurationMs = durationMs,
                ChapterCount = chapterCount,
                LibraryPath = Clean(libraryPath)
            };
            db.Add(audiobook);
        }
        else
        {
            audiobook.Title = cleanTitle;
            audiobook.Year ??= year;
            audiobook.Author ??= Clean(author);
            audiobook.Narrator ??= Clean(narrator);
            audiobook.Asin ??= Clean(asin);
            audiobook.DurationMs ??= durationMs;
            audiobook.ChapterCount ??= chapterCount;
            if (Clean(libraryPath) is { } path)
            {
                audiobook.LibraryPath = path;
            }

            audiobook.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var file in files)
        {
            var fileKey = FileKey(file.FileName);
            var existing = await db.Set<AudiobookFile>()
                .FirstOrDefaultAsync(x => x.AudiobookId == audiobook.Id && x.FileKey == fileKey, cancellationToken);
            if (existing is null)
            {
                db.Add(new AudiobookFile
                {
                    AudiobookId = audiobook.Id,
                    FileKey = fileKey,
                    FileName = file.FileName,
                    Format = file.Format,
                    StoragePath = file.StoragePath,
                    SizeBytes = file.SizeBytes,
                    DurationMs = file.DurationMs
                });
            }
            else
            {
                existing.FileName = file.FileName;
                existing.Format = file.Format;
                existing.StoragePath = file.StoragePath;
                existing.SizeBytes = file.SizeBytes;
                existing.DurationMs ??= file.DurationMs;
            }
        }

        if (files.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        var workId = await bridge.EnsureWorkForAudiobookAsync(audiobook, cancellationToken, requestedWorkId);
        return new AudiobookLibraryEntry(audiobook, workId);
    }

    /// <summary>The stable de-duplication key of an audiobook: folded title plus year so re-issues stay distinct.</summary>
    public static string AudiobookKey(string title, int? year)
    {
        var folded = new string((title ?? "")
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return year is int value ? $"{folded}:{value}" : folded;
    }

    private static string FileKey(string fileName) =>
        (fileName ?? "").Trim().ToLowerInvariant();

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
