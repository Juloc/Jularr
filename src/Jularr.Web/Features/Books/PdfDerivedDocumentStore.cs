using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jularr.Web.Features.Books;

/// <summary>
/// Derived PDF documents kept as files beside the imported PDFs, one per source file hash and analyzer version
/// (<c>{hash}.v{version}.json</c>). Like the translation caches they are disposable: deleting them loses nothing
/// the source PDF cannot rebuild, which a re-analysis does. Older analyzer versions stay until cleaned up so a
/// regression can be compared and rolled back later.
/// </summary>
public sealed partial class PdfDerivedDocumentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string rootPath;

    public PdfDerivedDocumentStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = Path.GetFullPath(rootPath);
    }

    public static string DefaultRoot => Path.Combine("/data", "books", "derived");

    public async Task SaveAsync(PdfDerivedDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(rootPath);
        var path = PathOf(document.SourceHash, document.AnalyzerVersion);
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// The newest stored derived document of a source file, or null when none exists or the file is unreadable
    /// (derived data is rebuildable, so a damaged file counts as missing).
    /// </summary>
    public async Task<PdfDerivedDocument?> TryLoadLatestAsync(string sourceHash, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootPath) || !HexHash().IsMatch(sourceHash))
        {
            return null;
        }

        var latest = Directory
            .EnumerateFiles(rootPath, SafeHash(sourceHash) + ".v*.json")
            .Select(path => (Path: path, Version: VersionOf(path)))
            .Where(entry => entry.Version is not null)
            .OrderByDescending(entry => entry.Version)
            .FirstOrDefault();
        if (latest.Path is null)
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(latest.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return await JsonSerializer.DeserializeAsync<PdfDerivedDocument>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string PathOf(string sourceHash, int version) =>
        Path.Combine(rootPath, $"{SafeHash(sourceHash)}.v{version.ToString(CultureInfo.InvariantCulture)}.json");

    private static int? VersionOf(string path) =>
        VersionedFile().Match(Path.GetFileName(path)) is { Success: true } match
            ? int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture)
            : null;

    private static string SafeHash(string sourceHash)
    {
        if (!HexHash().IsMatch(sourceHash))
        {
            throw new ArgumentException("The source hash must be a hex digest.", nameof(sourceHash));
        }

        return sourceHash.ToLowerInvariant();
    }

    [GeneratedRegex(@"\.v(?<version>\d{1,6})\.json$")]
    private static partial Regex VersionedFile();

    [GeneratedRegex(@"^[0-9a-fA-F]{16,128}$")]
    private static partial Regex HexHash();
}
