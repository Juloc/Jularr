using System.Text.Json;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Import;
using Jularr.Web.Features.Acquisition.Sabnzbd;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Books;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Operations;
using Jularr.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Books;

public sealed class IndexModel(
    BookCatalogService books,
    BookSearchCoordinator bookSearch,
    CurrentAccountContext account,
    AppDbContext db,
    DownloadClientStore downloadClients,
    SabnzbdDownloadService sabnzbd,
    OperationRunner operations,
    AcquisitionRequestService requests,
    AcquisitionAccessStore requestStore,
    MediaInboxImportService inboxes,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<IndexModel> logger) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public string Query { get; private set; } = "";
    public string TargetLanguage { get; private set; } = "id";
    public IReadOnlyList<BookLibraryItem> Library { get; private set; } = [];
    public bool IsOwner => account.IsOwner;
    public AcquisitionCapabilities Access { get; private set; } =
        AcquisitionCapabilities.Default(MediaAcquisitionKind.Book);
    /// <summary>The current profile's own book requests, newest first.</summary>
    public IReadOnlyList<AcquisitionRequest> MyRequests { get; private set; } = [];
    public IReadOnlyList<string> Genres => Library.SelectMany(book => book.Subjects).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(40).ToArray();
    public bool IsSabnzbdConfigured { get; private set; }
    public bool IsInboxConfigured { get; private set; }

    public async Task OnGetAsync(
        string? q,
        string? lang,
        CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Query = q?.Trim() ?? "";
        TargetLanguage = BookLanguageCatalog.Normalize(lang);
        Access = await requests.GetCapabilitiesAsync(MediaAcquisitionKind.Book, cancellationToken);
        MyRequests = await requestStore.ListAsync(MediaAcquisitionKind.Book, account.ProfileId, openOnly: false, limit: 50, cancellationToken);
        IsSabnzbdConfigured = Access.CanAddManually
            && (await downloadClients.LoadAllAsync(cancellationToken))
                .Any(entry => entry.Enabled && entry.Type == DownloadClientType.Sabnzbd);
        IsInboxConfigured = await inboxes.InboxAsync(MediaAcquisitionKind.Book, cancellationToken) is not null;

        Library = await books.GetLibraryAsync(
            account.ProfileId,
            TargetLanguage,
            cancellationToken);

        // Catalog search runs in the Add book dialog (OnGetSearchAsync); ?q= only pre-fills it.
    }

    public async Task<IActionResult> OnPostUploadAsync(
        IFormFile? book,
        CancellationToken cancellationToken)
    {
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!await CanAddManuallyAsync(cancellationToken))
        {
            return Forbid();
        }

        if (book is null || book.Length == 0)
        {
            TempData["Status"] = ui["books.index.chooseEpubFirst"];
            return RedirectToPage();
        }

        var format = BookFileFormats.FromPath(book.FileName);
        if (format is null)
        {
            TempData["Status"] = ui["books.index.epubOnly"];
            return RedirectToPage();
        }

        var isPdf = format == BookFileFormats.Pdf;
        try
        {
            var workId = await operations.RunAsync(
                new OperationDescriptor(
                    isPdf ? "book-pdf-upload-import" : "book-epub-upload-import",
                    "Books",
                    isPdf ? "Import uploaded PDF" : "Import uploaded EPUB",
                    book.FileName,
                    account.ProfileId,
                    OperationLane.Normal,
                    Retryable: false),
                async (operation, token) =>
                {
                    await operation.ReportAsync(
                        10,
                        isPdf ? "Storing uploaded PDF." : "Parsing uploaded EPUB.",
                        cancellationToken: token);

                    await using var stream = book.OpenReadStream();
                    return isPdf
                        ? await books.ImportUploadedPdfAsync(stream, book.FileName, token)
                        : await books.ImportUploadedEpubAsync(stream, book.FileName, token);
                },
                isPdf ? "Uploaded PDF imported." : "Uploaded EPUB imported.",
                cancellationToken);

            return RedirectToPage(
                "/Books/Library",
                new { id = workId, lang = "id" });
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Importing uploaded book {FileName} failed", book.FileName);
            TempData["Status"] = ui["books.index.uploadFailed"];
            return RedirectToPage();
        }
    }

    public async Task<IActionResult> OnPostRemoteEpubAsync(
        string? epubUrl,
        CancellationToken cancellationToken)
    {
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!await CanAddManuallyAsync(cancellationToken))
        {
            return Forbid();
        }

        var operationStore = new OperationStore(db);
        var operationId = await operationStore.CreateAsync(
            new OperationDescriptor(
                "remote-epub-import",
                "Books",
                "Download remote EPUB",
                ProfileId: account.ProfileId,
                Lane: OperationLane.Normal,
                IsDownload: true,
                Retryable: false),
            cancellationToken);

        await operationStore.MarkRunningAsync(operationId, cancellationToken);

        try
        {
            var workId = await books.ImportRemoteEpubAsync(
                epubUrl ?? "",
                cancellationToken);

            await operationStore.MarkSucceededAsync(
                operationId,
                "EPUB downloaded and imported.",
                cancellationToken);

            return RedirectToPage(
                "/Books/Library",
                new { id = workId, lang = "id" });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or HttpRequestException
                or TaskCanceledException)
        {
            await operationStore.MarkFailedAsync(
                operationId,
                $"{exception.GetType().Name}: {exception.Message}",
                CancellationToken.None);
            logger.LogError(exception, "Remote EPUB import failed for operation {OperationId}", operationId);
            TempData["Status"] = ui["books.index.remoteEpubFailed"];
            return RedirectToPage();
        }
    }

    public async Task<IActionResult> OnPostImportInboxAsync(
        CancellationToken cancellationToken)
    {
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!await CanAddManuallyAsync(cancellationToken))
        {
            return Forbid();
        }

        try
        {
            var imported = await inboxes.RunAsync(
                MediaAcquisitionKind.Book,
                account.ProfileId,
                cancellationToken);

            TempData["Status"] = imported.Imported == 0
                ? ui["books.index.inboxEmpty"]
                : ui.Format("books.index.inboxImported", ("count", imported.Imported));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Importing the book inbox for profile {ProfileId} failed", account.ProfileId);
            TempData["Status"] = ui["books.index.inboxImportFailed"];
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSabUrlAsync(
        string? nzbUrl,
        string? displayName,
        CancellationToken cancellationToken)
    {
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!await CanAddManuallyAsync(cancellationToken))
        {
            return Forbid();
        }

        var effectiveName = string.IsNullOrWhiteSpace(displayName)
            ? ui["books.index.defaultBookName"]
            : displayName.Trim();

        try
        {
            var outcome = await sabnzbd.SubmitUrlAsync(
                BookSabnzbdSubmission(effectiveName),
                SabnzbdDownloadService.ParseNzbUrl(nzbUrl),
                cancellationToken);
            TempData["Status"] = outcome.Message;
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Submitting SABnzbd URL download {DisplayName} failed", effectiveName);
            TempData["Status"] = ui["books.index.sabSubmitFailed"];
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSabFileAsync(
        IFormFile? nzb,
        CancellationToken cancellationToken)
    {
        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!await CanAddManuallyAsync(cancellationToken))
        {
            return Forbid();
        }

        if (nzb is null || nzb.Length == 0)
        {
            TempData["Status"] = ui["books.index.chooseNzbFirst"];
            return RedirectToPage();
        }

        try
        {
            await using var stream = nzb.OpenReadStream();
            var outcome = await sabnzbd.SubmitFileAsync(
                BookSabnzbdSubmission(nzb.FileName),
                stream,
                nzb.FileName,
                cancellationToken);
            TempData["Status"] = outcome.Message;
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Submitting SABnzbd file download {FileName} failed", nzb.FileName);
            TempData["Status"] = ui["books.index.sabSubmitFailed"];
        }

        return RedirectToPage();
    }

    /// <summary>Catalog search for the add dialog (JSON), annotated with library and request state.</summary>
    public async Task<IActionResult> OnGetSearchAsync(string? q, CancellationToken cancellationToken)
    {
        var query = q?.Trim() ?? "";
        if (query.Length < 2)
        {
            return new JsonResult(new { results = Array.Empty<object>() });
        }

        var ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        BookSearchResponse searchResult;
        try
        {
            searchResult = await bookSearch.SearchAsync(query, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new JsonResult(new { results = Array.Empty<object>(), error = ui["books.index.searchUnavailable"] });
        }

        var availabilityById = searchResult.Items.ToDictionary(
            result => result.Book.Id,
            result => result.Availability,
            StringComparer.Ordinal);
        var found = searchResult.Items
            .Select(result => result.Book)
            .Take(24)
            .ToArray();

        var hardcover = await new BookHardcoverAccountStore(
                dataProtectionProvider)
            .LoadAsync(account.ProfileId, cancellationToken);
        var items = (await books.EnrichHardcoverStatesAsync(
                found,
                hardcover,
                cancellationToken))
            .ToArray();
        var states = await new BookAddStateQuery(db, requestStore).GetAsync(
            items.Select(item => new BookAddLookup(item.Id, item.Title, item.Identities)).ToArray(),
            cancellationToken);

        return new JsonResult(new
        {
            results = items.Select(item =>
            {
                var state = states.GetValueOrDefault(item.Id) ?? BookAddState.None;
                return new
                {
                    // A request made through another provider's record of this work owns the row.
                    id = state.RequestCatalogId ?? item.Id,
                    item.Title,
                    item.Author,
                    item.CoverImageUrl,
                    covers = item.CoverCandidates,
                    year = item.FirstPublishYear,
                    language = item.Language,
                    isbn = item.Isbns.FirstOrDefault(),
                    identities = item.Identities,
                    summary = ShortSummary(item.Summary),
                    listState = ListStateKey(item.ExternalListState) is { } listStateKey
                        ? ui[listStateKey]
                        : null,
                    freeEdition = item.CanAcquire,
                    availability = availabilityById.TryGetValue(item.Id, out var availability)
                        ? new
                        {
                            directOrFree = availability.DirectOrFree,
                            opds = availability.Opds,
                            usenet = availability.Usenet,
                            usenetCandidates = availability.EligibleUsenetReleases
                        }
                        : new
                        {
                            directOrFree = item.CanAcquire,
                            opds = false,
                            usenet = false,
                            usenetCandidates = 0
                        },
                    state = StateJson(state),
                    // Only worth a picker once more than one provider record contributed (#405).
                    editions = item.Editions.Count > 1 ? item.Editions.Select(EditionJson) : null
                };
            }),
            warnings = searchResult.Warnings.Select(warning => new
            {
                source = warning.Source,
                warning.Message
            })
        });
    }

    /// <summary>One row of a work's edition picker: what is known about that provider record.</summary>
    private static object EditionJson(BookEditionSummary edition) => new
    {
        edition.Id,
        edition.Year,
        edition.Language,
        languageName = edition.Language is { } language ? BookLanguageCatalog.GetName(language) : null,
        edition.Publisher,
        edition.Isbn,
        edition.Format,
        source = edition.SourceName,
        cover = edition.CoverImageUrl
    };

    private static string? ListStateKey(string? state) =>
        state switch
        {
            BookListStates.WantToRead => "books.listState.wantToRead",
            BookListStates.Reading => "books.listState.reading",
            BookListStates.Read => "books.listState.read",
            BookListStates.Paused => "books.listState.paused",
            BookListStates.DidNotFinish => "books.listState.didNotFinish",
            _ => null
        };

    /// <summary>A result's description for the dialog, cut at a word near 280 characters.</summary>
    private static string? ShortSummary(string? summary)
    {
        var text = summary?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length <= 280)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', 280);
        return text[..(cut > 200 ? cut : 280)].TrimEnd(' ', ',', ';', ':', '.') + "…";
    }

    /// <summary>
    /// The current state of catalog books the open Add book dialog shows, so in-flight requests
    /// move on (searching, downloading, importing, in library) without reopening it.
    /// </summary>
    public async Task<IActionResult> OnGetStatusAsync(string[]? ids, CancellationToken cancellationToken)
    {
        var catalogIds = (ids ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(50)
            .ToArray();
        var states = await new BookAddStateQuery(db, requestStore).GetAsync(
            catalogIds.Select(id => new BookAddLookup(id)).ToArray(),
            cancellationToken);
        return new JsonResult(new
        {
            states = catalogIds.ToDictionary(id => id, id => StateJson(states.GetValueOrDefault(id) ?? BookAddState.None))
        });
    }

    /// <summary>Adds (automatic) or requests a catalog book according to the Books access policy.</summary>
    public async Task<IActionResult> OnPostAddAsync(
        string? catalogId,
        string? title,
        string? author,
        string? coverImageUrl,
        string? language,
        string? isbn,
        int? year,
        string[]? identities,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(catalogId) || string.IsNullOrWhiteSpace(title))
        {
            return BadRequest();
        }

        try
        {
            await requests.SubmitAsync(
                new AcquisitionRequestDraft(
                    MediaAcquisitionKind.Book,
                    BookCatalogService.CatalogRequestProvider,
                    catalogId.Trim(),
                    title.Trim(),
                    string.IsNullOrWhiteSpace(author) ? null : author.Trim(),
                    string.IsNullOrWhiteSpace(coverImageUrl) ? null : coverImageUrl.Trim(),
                    JsonSerializer.Serialize(new BookRequestPayload(catalogId.Trim(), title.Trim(), author?.Trim(), string.IsNullOrWhiteSpace(language) ? null : language.Trim(), string.IsNullOrWhiteSpace(isbn) ? null : isbn.Trim(), year, [.. (identities ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).Take(20)]), JsonSerializerOptions.Web)),
                cancellationToken);
        }
        catch (AcquisitionAccessDeniedException)
        {
            return Forbid();
        }

        var states = await new BookAddStateQuery(db, requestStore).GetAsync([new BookAddLookup(catalogId.Trim())], cancellationToken);
        return new JsonResult(StateJson(states.GetValueOrDefault(catalogId.Trim()) ?? BookAddState.None));
    }

    private static object StateJson(BookAddState state) => new
    {
        libraryWorkId = state.LibraryWorkId,
        requestStatus = state.RequestStatus,
        message = state.RequestMessage,
        progress = state.ProgressPercent,
        inFlight = state.IsInFlight
    };

    public async Task<IActionResult> OnPostCancelRequestAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await requests.CancelAsync(id, cancellationToken);
        }
        catch (AcquisitionAccessDeniedException)
        {
            return Forbid();
        }

        return RedirectToPage();
    }

    private async Task<bool> CanAddManuallyAsync(CancellationToken cancellationToken) =>
        (await requests.GetCapabilitiesAsync(MediaAcquisitionKind.Book, cancellationToken)).CanAddManually;

    private SabnzbdSubmission BookSabnzbdSubmission(string name) =>
        new(
            CompletedDownloadImportService.ManualDownloadOperationKind,
            "SABnzbd download",
            name,
            account.ProfileId,
            SabnzbdPurpose.Books,
            JobName: name);
}
