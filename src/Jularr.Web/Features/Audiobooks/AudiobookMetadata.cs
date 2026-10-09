using System.Globalization;
using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Features.Audiobooks;

/// <summary>
/// What a provider says about the audio edition of a Book Work, known before any file exists: its narrators, language, spoken duration, ASIN and cover.
/// One row per audio <see cref="WorkEdition"/>; the file-derived facts of an imported <see cref="Audiobook"/> stay separate.
/// </summary>
public sealed class AudiobookEditionMetadata
{
    public Guid EditionId { get; set; }

    public string Provider { get; set; } = "";

    /// <summary>The provider's id of the recording project, so a refresh addresses the same one.</summary>
    public string ExternalId { get; set; } = "";

    public string? Title { get; set; }

    public string[] Narrators { get; set; } = [];

    public int? DurationSeconds { get; set; }

    public string? Asin { get; set; }

    public string? CoverUrl { get; set; }

    public string? SourceUrl { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A recording project of the provider: a candidate the owner can confirm for a Book.</summary>
public sealed record AudiobookCandidate(string Provider, string ExternalId, string Title, string? Language, IReadOnlyList<string> Authors, IReadOnlyList<string> Narrators, int? DurationSeconds, string? CoverUrl, string? SourceUrl);

public sealed class AudiobookMetadataException(string message, Exception? innerException = null) : Exception(message, innerException);

public interface IAudiobookMetadataProvider
{
    string Key { get; }

    Task<IReadOnlyList<AudiobookCandidate>> SearchAsync(string title, CancellationToken cancellationToken);

    Task<AudiobookCandidate?> GetAsync(string externalId, CancellationToken cancellationToken);
}

/// <summary>
/// LibriVox (public-domain recordings; the documented read-only catalog feed, no key). Calls run through the shared provider framework with bounded retries.
/// A title search is anchored to the start of the title; the narrators are the readers of the project's sections.
/// </summary>
public sealed class LibriVoxClient(HttpClient httpClient, ProviderExecutor executor) : IAudiobookMetadataProvider, IExternalProvider
{
    public const string ProviderKey = "librivox";
    public const string BaseUrl = "https://librivox.org/api/feed/audiobooks";
    private const int SearchLimit = 10;

    public static readonly ProviderExecutionPolicy ExecutionPolicy = new() { MaxAttempts = 3, MinSpacing = TimeSpan.FromSeconds(1) };

    public string Key => ProviderKey;

    public ExternalProviderDescriptor Descriptor { get; } = new(ProviderKey, "LibriVox", ProviderCapabilities.Metadata | ProviderCapabilities.Search | ProviderCapabilities.Audiobooks);

    public async Task<IReadOnlyList<AudiobookCandidate>> SearchAsync(string title, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(title)
            ? []
            : await QueryAsync($"?title=^{Uri.EscapeDataString(title.Trim())}&format=json&extended=1&coverart=1&limit={SearchLimit}", cancellationToken);

    public async Task<AudiobookCandidate?> GetAsync(string externalId, CancellationToken cancellationToken) =>
        !externalId.All(char.IsAsciiDigit) || externalId.Length == 0
            ? null
            : (await QueryAsync($"?id={externalId}&format=json&extended=1&coverart=1", cancellationToken)).FirstOrDefault();

    private async Task<IReadOnlyList<AudiobookCandidate>> QueryAsync(string query, CancellationToken cancellationToken)
    {
        using var response = await executor.SendAsync(
            ProviderKey,
            httpClient,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUrl + query));
                request.Headers.Accept.ParseAdd("application/json");
                return request;
            },
            ExecutionPolicy,
            cancellationToken);

        // LibriVox answers HTTP 404 with an error body when nothing matches; that is an empty result, not a failure.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AudiobookMetadataException($"LibriVox answered HTTP {(int)response.StatusCode}.");
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("books", out var books) && books.ValueKind == JsonValueKind.Array
                ? [.. books.EnumerateArray().Select(Read).OfType<AudiobookCandidate>()]
                : [];
        }
        catch (JsonException exception)
        {
            throw new AudiobookMetadataException("LibriVox answered with a malformed document.", exception);
        }
    }

    private static AudiobookCandidate? Read(JsonElement book)
    {
        if (Text(book, "id") is not { } id || Text(book, "title") is not { } title)
        {
            return null;
        }

        var authors = book.TryGetProperty("authors", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(author => string.Join(' ', new[] { Text(author, "first_name"), Text(author, "last_name") }.Where(part => part is not null))).Where(name => name.Length > 0).ToArray()
            : [];
        var narrators = book.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array
            ? sections.EnumerateArray()
                .SelectMany(section => section.TryGetProperty("readers", out var readers) && readers.ValueKind == JsonValueKind.Array ? readers.EnumerateArray() : [])
                .Select(reader => Text(reader, "display_name"))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        var seconds = int.TryParse(Text(book, "totaltimesecs"), NumberStyles.None, CultureInfo.InvariantCulture, out var total) && total > 0 ? total : (int?)null;
        return new AudiobookCandidate(ProviderKey, id, title, Text(book, "language"), authors, narrators, seconds, Text(book, "coverart_jpg"), Text(book, "url_librivox"));
    }

    // LibriVox sends numbers as strings in some fields and as numbers in others; both read as text.
    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString()!.Trim(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;
}

/// <summary>
/// Stores what a provider says about the audio edition of a Work once the owner confirmed which recording it is: a title match alone never attaches one.
/// The edition's language is taken from the provider only while it is still unknown.
/// </summary>
public sealed class AudiobookMetadataService(AppDbContext db, IEnumerable<IAudiobookMetadataProvider> providers)
{
    public async Task<IReadOnlyList<AudiobookCandidate>> FindAsync(Guid workId, CancellationToken cancellationToken)
    {
        var title = await db.Works.AsNoTracking().Where(work => work.Id == workId && work.MediaType == WorkMediaType.Book).Select(work => work.CanonicalTitle).FirstOrDefaultAsync(cancellationToken);
        return title is null ? [] : [.. (await Task.WhenAll(providers.Select(provider => provider.SearchAsync(title, cancellationToken)))).SelectMany(found => found)];
    }

    public async Task<AudiobookEditionMetadata?> AttachAsync(Guid workId, string provider, string externalId, CancellationToken cancellationToken)
    {
        var source = providers.FirstOrDefault(item => item.Key == provider);
        if (source is null || !await db.Works.AnyAsync(work => work.Id == workId && work.MediaType == WorkMediaType.Book, cancellationToken) || await source.GetAsync(externalId, cancellationToken) is not { } candidate)
        {
            return null;
        }

        var editionId = await AudiobookEditions.EnsureAsync(db, workId, cancellationToken);
        var metadata = await db.AudiobookEditionMetadata.FindAsync([editionId], cancellationToken);
        if (metadata is null)
        {
            metadata = new AudiobookEditionMetadata { EditionId = editionId };
            db.AudiobookEditionMetadata.Add(metadata);
        }

        (metadata.Provider, metadata.ExternalId, metadata.Title, metadata.Narrators, metadata.DurationSeconds, metadata.CoverUrl, metadata.SourceUrl, metadata.UpdatedAt) =
            (candidate.Provider, candidate.ExternalId, candidate.Title, [.. candidate.Narrators], candidate.DurationSeconds, candidate.CoverUrl, candidate.SourceUrl, DateTime.UtcNow);
        if (LanguageTag(candidate.Language) is { } tag && await db.WorkEditions.FirstAsync(edition => edition.Id == editionId, cancellationToken) is { Language: "und" } edition)
        {
            edition.Language = tag;
        }

        await db.SaveChangesAsync(cancellationToken);
        return metadata;
    }

    public async Task<AudiobookEditionMetadata?> GetAsync(Guid workId, CancellationToken cancellationToken) =>
        await (from edition in db.WorkEditions.AsNoTracking()
               join metadata in db.AudiobookEditionMetadata.AsNoTracking() on edition.Id equals metadata.EditionId
               where edition.WorkId == workId && edition.EditionKey == LegacyWorkBridge.AudiobookEditionKey
               select metadata).FirstOrDefaultAsync(cancellationToken);

    /// <summary>The two-letter tag of a language the provider names in English ("English" becomes "en"); null for a name no culture carries.</summary>
    public static string? LanguageTag(string? englishName) =>
        string.IsNullOrWhiteSpace(englishName)
            ? null
            : CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(culture => culture.TwoLetterISOLanguageName.Length == 2 && culture.EnglishName.Equals(englishName.Trim(), StringComparison.OrdinalIgnoreCase))?.TwoLetterISOLanguageName;
}
