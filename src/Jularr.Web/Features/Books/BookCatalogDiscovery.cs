using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Jularr.Web.Features.Books;

public sealed partial class BookCatalogService
{
    private const int MaxRemoteCoverBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan DiscoveryProviderTimeout =
        TimeSpan.FromSeconds(7);

    /// <summary>The metadata providers whose cover URLs Jularr downloads into local artwork.</summary>
    private static readonly HashSet<string> AllowedCoverHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "books.google.com",
            "books.googleusercontent.com",
            "covers.openlibrary.org",
            "www.gutenberg.org",
            "gutenberg.org"
        };

    /// <summary>
    /// Where an allowed cover URL may end up after redirects: the provider hosts, plus the
    /// Internet Archive, which stores and serves Open Library covers.
    /// </summary>
    private static bool IsAllowedCoverTarget(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && (AllowedCoverHosts.Contains(uri.Host)
            || uri.Host.Equals("archive.org", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<BookCatalogItem>> CaptureCatalogAsync(
        Func<CancellationToken, Task<IReadOnlyList<BookCatalogItem>>> action,
        CancellationToken cancellationToken,
        bool fallbackToEmpty = true) =>
        (await CaptureCatalogResultAsync(action, cancellationToken, fallbackToEmpty)).Items;

    /// <summary>
    /// Runs one catalog call within the discovery timeout. A failure yields no titles and says so, so a caller that must tell "nothing found"
    /// from "did not answer" (Discover) can; the others keep treating a failed catalog as one that contributes nothing.
    /// </summary>
    private async Task<(IReadOnlyList<BookCatalogItem> Items, bool Failed)> CaptureCatalogResultAsync(
        Func<CancellationToken, Task<IReadOnlyList<BookCatalogItem>>> action,
        CancellationToken cancellationToken,
        bool fallbackToEmpty = true)
    {
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeout.CancelAfter(DiscoveryProviderTimeout);

        try
        {
            return (await action(timeout.Token), false);
        }
        catch (TaskCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return ([], true);
        }
        catch (HttpRequestException)
        {
            return ([], true);
        }
        catch (InvalidOperationException)
            when (fallbackToEmpty)
        {
            return ([], true);
        }
    }

    /// <summary>The titles of a browse row; a row whose catalog did not answer is a failure, never a row of nothing.</summary>
    private static IReadOnlyList<BookCatalogItem> OrThrow((IReadOnlyList<BookCatalogItem> Items, bool Failed) result, string row) =>
        result.Failed ? throw new HttpRequestException($"The {row} books listing did not answer.") : result.Items;

    private async Task<IReadOnlyList<BookCatalogItem>> BrowseTrendingBooksAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var daily = await CaptureCatalogResultAsync(
            token => BrowseOpenLibraryTrendingAsync("daily", offset, limit, token),
            cancellationToken);

        if (daily.Items.Count > 0)
        {
            return await EnrichTrendingWithGoogleAsync(
                daily.Items,
                cancellationToken);
        }

        // Still preserve trending semantics when the daily window happens to be
        // unavailable. Do not silently substitute all-time Gutenberg downloads.
        var weekly = await CaptureCatalogResultAsync(
            token => BrowseOpenLibraryTrendingAsync("weekly", offset, limit, token),
            cancellationToken);
        return await EnrichTrendingWithGoogleAsync(
            OrThrow(daily.Failed ? weekly : (weekly.Items, false), "trending"),
            cancellationToken);
    }

    /// <summary>
    /// The "Popular" row (#371): Open Library's own catalog-wide edition-count ranking
    /// (<c>sort=editions</c> against the unscoped catalog query), a real, enduring-popularity
    /// signal distinct from /trending's recent-activity one. Verified live against Open Library
    /// (Bible, Pride and Prejudice and similar long-standing works lead it); never Gutenberg
    /// download counts.
    /// </summary>
    private async Task<IReadOnlyList<BookCatalogItem>> BrowseTopBooksAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var items = OrThrow(await CaptureCatalogResultAsync(token => SearchOpenLibraryPopularAsync(offset, limit, token), cancellationToken), "popular");
        return await EnrichTrendingWithGoogleAsync(items, cancellationToken);
    }

    private async Task<IReadOnlyList<BookCatalogItem>> SearchOpenLibraryPopularAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            "https://openlibrary.org/search.json"
            + "?q=" + Uri.EscapeDataString("*:*")
            + "&sort=editions"
            + "&fields=key,title,author_name,cover_i,first_publish_year,subject,isbn,edition_count,language"
            + $"&limit={limit}&offset={offset}");

        var response = await GetJsonAsync<OpenLibrarySearchResponse>(
            uri,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Open Library popular listing returned no data.");

        return response.Docs
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Key)
                && !string.IsNullOrWhiteSpace(x.Title)
                && x.Key.StartsWith("/works/", StringComparison.Ordinal))
            .Take(limit)
            .Select(MapOpenLibrarySearch)
            .ToArray();
    }

    /// <summary>
    /// The "New" row (#371): Open Library's subject-scoped recent listing
    /// (<c>/subjects/fiction.json?sort=new</c>). The unscoped catalog query
    /// (<c>/search.json?q=*:*&amp;sort=new</c>) was verified live to surface spam/placeholder
    /// records (implausible publish years, markup injected into titles); subject-scoping to a
    /// broad, actively-catalogued subject avoids that, and <see cref="IsPlausibleBookTitle"/> plus
    /// the publish-year sanity check in <see cref="MapOpenLibrarySubjectWork"/> defend against it
    /// regardless.
    /// </summary>
    private async Task<IReadOnlyList<BookCatalogItem>> BrowseNewBooksAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var items = OrThrow(await CaptureCatalogResultAsync(token => SearchOpenLibraryRecentAsync("fiction", offset, limit, token), cancellationToken), "new");
        return await EnrichTrendingWithGoogleAsync(items, cancellationToken);
    }

    private async Task<IReadOnlyList<BookCatalogItem>> SearchOpenLibraryRecentAsync(
        string subject,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"https://openlibrary.org/subjects/{Uri.EscapeDataString(subject)}.json"
            + "?sort=new"
            + $"&limit={limit}&offset={offset}");

        var response = await GetJsonAsync<OpenLibrarySubjectResponse>(
            uri,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Open Library recent listing returned no data.");

        var currentYear = DateTime.UtcNow.Year;
        return (response.Works ?? [])
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Key)
                && !string.IsNullOrWhiteSpace(x.Title)
                && x.Key.StartsWith("/works/", StringComparison.Ordinal)
                && IsPlausibleBookTitle(x.Title!))
            .Take(limit)
            .Select(x => MapOpenLibrarySubjectWork(x, currentYear))
            .ToArray();
    }

    /// <summary>
    /// A cheap data-quality guard for community-editable subject listings: rejects markup and
    /// implausibly long "titles" (#371 found an injected <c>&lt;iframe&gt;</c> in an unscoped
    /// listing). Rendering already HTML-encodes every title, so this is defence in depth against
    /// bad data, not an XSS fix.
    /// </summary>
    private static bool IsPlausibleBookTitle(string title) =>
        title.Length is > 0 and <= 300
        && !title.Contains('<')
        && !title.Contains('>');

    private async Task<IReadOnlyList<BookCatalogItem>> BrowseOpenLibraryTrendingAsync(
        string window,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync<OpenLibraryTrendingResponse>(
            new Uri(
                $"https://openlibrary.org/trending/{window}.json?limit={limit}&offset={offset}"),
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Open Library trending returned no data.");

        return (response.Works ?? [])
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Key)
                && !string.IsNullOrWhiteSpace(x.Title)
                && x.Key.StartsWith("/works/", StringComparison.Ordinal))
            .Select(MapOpenLibrarySearch)
            .Take(SearchLimit)
            .ToArray();
    }

    private async Task<IReadOnlyList<BookCatalogItem>> EnrichTrendingWithGoogleAsync(
        IReadOnlyList<BookCatalogItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return items;
        }

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var gate = new SemaphoreSlim(8, 8);

        var tasks = items.Select(async item =>
        {
            try
            {
                await gate.WaitAsync(timeout.Token);
                try
                {
                    var isbn = item.Isbns.FirstOrDefault();
                    var query = isbn is not null
                        ? "isbn:" + isbn
                        : "intitle:" + item.Title
                            + (string.IsNullOrWhiteSpace(item.Author)
                                ? ""
                                : " inauthor:" + item.Author);

                    var candidates = (await SearchGoogleVolumesAsync(
                            query,
                            5,
                            timeout.Token))
                        .Select(MapGoogleBook)
                        .Where(candidate =>
                            !string.IsNullOrWhiteSpace(candidate.CoverImageUrl))
                        .ToArray();

                    var exact = isbn is null
                        ? candidates.FirstOrDefault(candidate =>
                            StrongBookMatch(item.Title, item.Author, candidate))
                        : candidates.FirstOrDefault(candidate =>
                            candidate.Isbns.Contains(
                                isbn,
                                StringComparer.Ordinal));

                    return exact is null
                        ? item
                        : BookWorkSearch.Combine(item, exact);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested
                && exception is HttpRequestException
                    or TaskCanceledException
                    or InvalidOperationException
                    or OperationCanceledException)
            {
                return item;
            }
        }).ToArray();

        return await Task.WhenAll(tasks);
    }

    private async Task<IReadOnlyList<BookCatalogItem>> SearchGoogleBooksAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var response = await SearchGoogleVolumesAsync(
            query,
            24,
            cancellationToken);

        return response
            .Select(MapGoogleBook)
            .Take(SearchLimit)
            .ToArray();
    }

    private async Task<IReadOnlyList<GoogleVolume>> SearchGoogleVolumesAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync<GoogleVolumesResponse>(
            BuildGoogleBooksUri(
                "volumes",
                new Dictionary<string, string?>
                {
                    ["q"] = query,
                    ["maxResults"] = Math.Clamp(limit, 1, 40).ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["printType"] = "books",
                    ["orderBy"] = "relevance",
                    ["projection"] = "full"
                }),
            cancellationToken);

        return (response?.Items ?? [])
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Id)
                && !string.IsNullOrWhiteSpace(x.VolumeInfo?.Title))
            .ToArray();
    }

    private async Task<BookCatalogItem?> GetGoogleBooksAsync(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            var volume = await GetJsonAsync<GoogleVolume>(
                BuildGoogleBooksUri(
                    "volumes/" + Uri.EscapeDataString(id),
                    new Dictionary<string, string?>()),
                cancellationToken);

            return volume is null
                || string.IsNullOrWhiteSpace(volume.VolumeInfo?.Title)
                ? null
                : MapGoogleBook(volume);
        }
        catch (HttpRequestException exception)
            when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private Uri BuildGoogleBooksUri(
        string path,
        IReadOnlyDictionary<string, string?> query)
    {
        var parameters = new List<string>();

        foreach (var pair in query)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            parameters.Add(
                Uri.EscapeDataString(pair.Key)
                + "="
                + Uri.EscapeDataString(pair.Value));
        }

        var apiKey = configuration["Books:GoogleBooks:ApiKey"]?.Trim();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            parameters.Add(
                "key=" + Uri.EscapeDataString(apiKey));
        }

        var suffix = parameters.Count == 0
            ? ""
            : "?" + string.Join("&", parameters);

        return new Uri(
            "https://www.googleapis.com/books/v1/"
            + path
            + suffix);
    }

    private static BookCatalogItem MapGoogleBook(
        GoogleVolume volume)
    {
        var info = volume.VolumeInfo
            ?? new GoogleVolumeInfo();

        var author = info.Authors is { Length: > 0 }
            ? string.Join(
                ", ",
                info.Authors.Where(x =>
                    !string.IsNullOrWhiteSpace(x)))
            : null;

        // Largest first; search results usually only carry the thumbnails.
        IReadOnlyList<string> covers = new[]
            {
                info.ImageLinks?.ExtraLarge,
                info.ImageLinks?.Large,
                info.ImageLinks?.Medium,
                info.ImageLinks?.Small,
                info.ImageLinks?.Thumbnail,
                info.ImageLinks?.SmallThumbnail
            }
            .Select(NormalizeGoogleCoverUrl)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var sourceUrl = FirstNonEmpty(
                info.CanonicalVolumeLink,
                info.InfoLink)
            ?? "https://books.google.com/books?id="
                + Uri.EscapeDataString(volume.Id ?? "");

        return new BookCatalogItem(
            "gb-" + (volume.Id ?? ""),
            info.Title?.Trim() ?? "Untitled book",
            string.IsNullOrWhiteSpace(author)
                ? null
                : author,
            CleanGoogleDescription(info.Description),
            covers.FirstOrDefault(),
            (info.Categories ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Take(16)
                .ToArray(),
            ParseYear(info.PublishedDate),
            null,
            null,
            sourceUrl,
            BookWorkSearch.GoogleBooksSource,
            null)
        {
            Isbns = (info.IndustryIdentifiers ?? [])
                .Where(x => x.Type is "ISBN_13" or "ISBN_10")
                .Select(x => BookWorkSearch.NormalizeIsbn(x.Identifier))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            CoverCandidates = covers,
            Publisher = info.Publisher?.Trim(),
            PublishedDate = info.PublishedDate?.Trim(),
            // Google Books volumes are edition-specific and already tag their own language.
            Language = BookWorkSearch.NormalizeLanguageTag(info.Language)
        };
    }

    private static string? FirstNonEmpty(
        string? first,
        string? second,
        string? third,
        params string?[] remaining)
    {
        foreach (var value in new[] { first, second, third }.Concat(remaining))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? NormalizeGoogleCoverUrl(string? cover)
    {
        if (string.IsNullOrWhiteSpace(cover))
        {
            return null;
        }

        var normalized = cover.Trim();
        if (normalized.StartsWith(
                "http://",
                StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://" + normalized["http://".Length..];
        }

        // "edge=curl" paints a page-curl effect onto the artwork.
        return normalized.Replace("&edge=curl", "", StringComparison.Ordinal);
    }

    private Task<bool> TryPersistPreferredCoverAsync(
        Guid workId,
        ParsedEpubBook parsed,
        string? catalogFallback,
        string? knownStoragePath,
        CancellationToken cancellationToken) =>
        TryPersistPreferredCoverAsync(
            workId,
            parsed.Title,
            parsed.Author,
            parsed.Isbn10,
            parsed.Isbn13,
            parsed.CoverBytes,
            parsed.CoverMediaType,
            catalogFallback,
            knownStoragePath,
            cancellationToken);

    private async Task<bool> TryPersistPreferredCoverAsync(
        Guid workId,
        string title,
        string? author,
        string? isbn10,
        string? isbn13,
        byte[]? embeddedCover,
        string? embeddedMediaType,
        string? catalogFallback,
        string? knownStoragePath,
        CancellationToken cancellationToken)
    {
        // Cover precedence (#371): the exact edition on Google Books, then the
        // imported file's own cover (it is the actual edition), then a strong
        // Google title/author match, then the catalog's artwork. Artwork
        // enrichment is optional: importing the owned book still succeeds when a
        // metadata provider is unavailable.
        var exactCover = await OptionalCoverLookupAsync(
            token => FindExactGoogleCoverAsync(isbn10, isbn13, token),
            cancellationToken);
        if (await TryCacheRemoteCoverAsync(workId, exactCover, knownStoragePath, cancellationToken))
        {
            return true;
        }

        if (embeddedCover is { Length: > 0 }
            && !string.IsNullOrWhiteSpace(embeddedMediaType)
            && await SaveLocalCoverAsync(
                workId,
                embeddedCover,
                embeddedMediaType,
                identity: "embedded",
                knownStoragePath,
                cancellationToken) is not null)
        {
            return true;
        }

        var matchedCover = await OptionalCoverLookupAsync(
            token => FindMatchingGoogleCoverAsync(title, author, token),
            cancellationToken);
        if (!string.Equals(matchedCover, exactCover, StringComparison.Ordinal)
            && await TryCacheRemoteCoverAsync(workId, matchedCover, knownStoragePath, cancellationToken))
        {
            return true;
        }

        return !string.Equals(catalogFallback, exactCover, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(catalogFallback, matchedCover, StringComparison.OrdinalIgnoreCase)
            && await TryCacheRemoteCoverAsync(workId, catalogFallback, knownStoragePath, cancellationToken);
    }

    private static async Task<string?> OptionalCoverLookupAsync(
        Func<CancellationToken, Task<string?>> lookup,
        CancellationToken cancellationToken)
    {
        try
        {
            return await lookup(cancellationToken);
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is HttpRequestException
                or TaskCanceledException
                or InvalidOperationException
                or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task<string?> FindExactGoogleCoverAsync(
        string? isbn10,
        string? isbn13,
        CancellationToken cancellationToken)
    {
        foreach (var isbn in new[] { isbn13, isbn10 }
                     .Select(BookWorkSearch.NormalizeIsbn)
                     .OfType<string>()
                     .Distinct(StringComparer.Ordinal))
        {
            var exact = await SearchGoogleVolumesAsync(
                "isbn:" + isbn,
                10,
                cancellationToken);
            var exactCover = exact
                .Select(MapGoogleBook)
                .FirstOrDefault(x =>
                    x.Isbns.Contains(isbn, StringComparer.Ordinal)
                    && !string.IsNullOrWhiteSpace(x.CoverImageUrl))
                ?.CoverImageUrl;
            if (!string.IsNullOrWhiteSpace(exactCover))
            {
                return exactCover;
            }
        }

        return null;
    }

    private async Task<string?> FindMatchingGoogleCoverAsync(
        string title,
        string? author,
        CancellationToken cancellationToken)
    {
        title = title.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var query = "intitle:" + title;
        if (!string.IsNullOrWhiteSpace(author))
        {
            query += " inauthor:" + author.Trim();
        }

        // Several editions match: the current one (newest) has the current artwork.
        return (await SearchGoogleVolumesAsync(
                query,
                24,
                cancellationToken))
            .Select(MapGoogleBook)
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.CoverImageUrl)
                && StrongBookMatch(title, author, x))
            .OrderByDescending(x => ParseYear(x.PublishedDate)
                ?? x.FirstPublishYear
                ?? 0)
            .Select(x => x.CoverImageUrl)
            .FirstOrDefault();
    }

    /// <summary>
    /// Whether a Google volume is the same work as the book the cover is for, by the same
    /// identity rule the search merge uses: similar titles alone never match.
    /// </summary>
    private static bool StrongBookMatch(
        string expectedTitle,
        string? expectedAuthor,
        BookCatalogItem candidate) =>
        BookWorkSearch.SameWork(
            expectedTitle,
            expectedAuthor,
            candidate.Title,
            candidate.Author);

    private async Task<bool> TryCacheRemoteCoverAsync(
        Guid workId,
        string? coverUrl,
        string? knownStoragePath,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(
                coverUrl,
                UriKind.Absolute,
                out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !AllowedCoverHosts.Contains(uri.Host))
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await SendAsyncWithTimeout(
                request,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is not null && !IsAllowedCoverTarget(finalUri))
            {
                return false;
            }

            var length = response.Content.Headers.ContentLength;
            if (length is > MaxRemoteCoverBytes)
            {
                return false;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null
                || mediaType is not (
                    "image/jpeg"
                    or "image/jpg"
                    or "image/png"
                    or "image/webp"
                    or "image/gif"))
            {
                return false;
            }

            await using var source = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            using var copy = await CopyToMemoryBoundedAsync(
                source,
                MaxRemoteCoverBytes,
                cancellationToken);
            var bytes = copy.ToArray();

            if (!LooksLikeImage(bytes, mediaType))
            {
                return false;
            }

            return await SaveLocalCoverAsync(
                    workId,
                    bytes,
                    mediaType,
                    identity: coverUrl,
                    knownStoragePath,
                    cancellationToken)
                is not null;
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is HttpRequestException
                or TaskCanceledException
                or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool LooksLikeImage(
        byte[] bytes,
        string mediaType)
    {
        if (bytes.Length < 12)
        {
            return false;
        }

        return mediaType switch
        {
            "image/jpeg" or "image/jpg" =>
                bytes[0] == 0xFF && bytes[1] == 0xD8,
            "image/png" =>
                bytes[0] == 0x89
                && bytes[1] == 0x50
                && bytes[2] == 0x4E
                && bytes[3] == 0x47,
            "image/gif" =>
                bytes[0] == (byte)'G'
                && bytes[1] == (byte)'I'
                && bytes[2] == (byte)'F',
            "image/webp" =>
                bytes[0] == (byte)'R'
                && bytes[1] == (byte)'I'
                && bytes[2] == (byte)'F'
                && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W'
                && bytes[9] == (byte)'E'
                && bytes[10] == (byte)'B'
                && bytes[11] == (byte)'P',
            _ => false
        };
    }

    private static string? CleanGoogleDescription(
        string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var decoded = WebUtility.HtmlDecode(description);
        var withoutTags = Regex.Replace(
            decoded,
            @"<[^>]+>",
            " ");
        var clean = Regex.Replace(
                withoutTags,
                @"\s+",
                " ")
            .Trim();

        return clean.Length <= 4000
            ? clean
            : clean[..4000].TrimEnd();
    }

    private static bool TryParseGoogleBooksId(
        string id,
        out string googleId)
    {
        googleId = "";

        if (!id.StartsWith(
                "gb-",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = id[3..].Trim();
        if (candidate.Length is < 1 or > 128
            || !Regex.IsMatch(
                candidate,
                @"^[A-Za-z0-9_-]+$"))
        {
            return false;
        }

        googleId = candidate;
        return true;
    }

    private sealed record OpenLibraryTrendingResponse(
        [property: JsonPropertyName("works")]
        OpenLibrarySearchDoc[]? Works);

    private sealed record GoogleVolumesResponse(
        [property: JsonPropertyName("items")]
        GoogleVolume[]? Items);

    private sealed record GoogleVolume(
        [property: JsonPropertyName("id")]
        string? Id,
        [property: JsonPropertyName("volumeInfo")]
        GoogleVolumeInfo? VolumeInfo);

    private sealed record GoogleVolumeInfo
    {
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("authors")]
        public string[]? Authors { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("categories")]
        public string[]? Categories { get; init; }

        [JsonPropertyName("publishedDate")]
        public string? PublishedDate { get; init; }

        [JsonPropertyName("publisher")]
        public string? Publisher { get; init; }

        [JsonPropertyName("language")]
        public string? Language { get; init; }

        [JsonPropertyName("industryIdentifiers")]
        public GoogleIndustryIdentifier[]? IndustryIdentifiers { get; init; }

        [JsonPropertyName("imageLinks")]
        public GoogleImageLinks? ImageLinks { get; init; }

        [JsonPropertyName("infoLink")]
        public string? InfoLink { get; init; }

        [JsonPropertyName("canonicalVolumeLink")]
        public string? CanonicalVolumeLink { get; init; }
    }

    private sealed record GoogleIndustryIdentifier(
        [property: JsonPropertyName("type")]
        string? Type,
        [property: JsonPropertyName("identifier")]
        string? Identifier);

    private sealed record GoogleImageLinks
    {
        [JsonPropertyName("smallThumbnail")]
        public string? SmallThumbnail { get; init; }

        [JsonPropertyName("thumbnail")]
        public string? Thumbnail { get; init; }

        [JsonPropertyName("small")]
        public string? Small { get; init; }

        [JsonPropertyName("medium")]
        public string? Medium { get; init; }

        [JsonPropertyName("large")]
        public string? Large { get; init; }

        [JsonPropertyName("extraLarge")]
        public string? ExtraLarge { get; init; }
    }
}
