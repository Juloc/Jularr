using System.Security.Cryptography;
using System.Text;
using Jularr.Web.Features.Ai;

namespace Jularr.Web.Features.Books;

public sealed class BookTranslationChunkStore
{
    private readonly string rootPath;

    public BookTranslationChunkStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = Path.GetFullPath(rootPath);
    }

    public static string DefaultRoot =>
        Path.Combine("/data", "books", "translation-chunks");

    public async Task<string?> TryLoadAsync(
        Guid workId,
        Guid chapterId,
        string targetLanguage,
        string sourceHash,
        int promptVersion,
        AiTranslationMode mode,
        int index,
        string sourceChunk,
        CancellationToken cancellationToken)
    {
        var path = GetPath(
            workId,
            chapterId,
            targetLanguage,
            sourceHash,
            promptVersion,
            mode,
            index,
            sourceChunk);

        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > 2 * 1024 * 1024)
        {
            return null;
        }

        var value = await File.ReadAllTextAsync(path, cancellationToken);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    public async Task SaveAsync(
        Guid workId,
        Guid chapterId,
        string targetLanguage,
        string sourceHash,
        int promptVersion,
        AiTranslationMode mode,
        int index,
        string sourceChunk,
        string translatedChunk,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translatedChunk);

        var path = GetPath(
            workId,
            chapterId,
            targetLanguage,
            sourceHash,
            promptVersion,
            mode,
            index,
            sourceChunk);

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "Translation chunk-cache directory is unavailable.");

        Directory.CreateDirectory(directory);
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";

        try
        {
            await File.WriteAllTextAsync(
                temporary,
                translatedChunk.Trim(),
                cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public Task ClearAsync(
        Guid workId,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = Path.Combine(
            rootPath,
            workId.ToString("N"),
            SafeSegment(targetLanguage));

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        return Task.CompletedTask;
    }

    private string GetPath(
        Guid workId,
        Guid chapterId,
        string targetLanguage,
        string sourceHash,
        int promptVersion,
        AiTranslationMode mode,
        int index,
        string sourceChunk)
    {
        // The key deliberately excludes the story-memory context: that context grows as other chapters finish,
        // and a retried chapter must resume its finished segments instead of translating them again.
        var chunkHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceChunk)));

        return Path.Combine(
            rootPath,
            workId.ToString("N"),
            SafeSegment(targetLanguage),
            chapterId.ToString("N"),
            SafeSegment(sourceHash),
            $"v{promptVersion}",
            mode.ToString().ToLowerInvariant(),
            $"{index:D4}-{chunkHash}.txt");
    }

    private static string SafeSegment(string value)
    {
        var cleaned = new string(
            value
                .Where(character =>
                    char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '_')
                .ToArray());

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            throw new InvalidOperationException(
                "Translation cache key contains no safe characters.");
        }

        return cleaned.Length <= 100
            ? cleaned
            : cleaned[..100];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
