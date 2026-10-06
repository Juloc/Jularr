using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Collections;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;
using Jularr.Web.Ui;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Library;

public sealed class IndexModel(
    AppDbContext db,
    CurrentAccountContext currentAccount,
    CollectionService collections,
    IAppShellService appShell,
    ILogger<IndexModel> logger,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    /// <summary>What the address asks for: section, sort, layout and filters.</summary>
    public LibraryBrowseQuery Query { get; private set; } = new();

    public LibraryPageState State { get; private set; } = LibraryPageState.Ready;

    /// <summary>The titles that pass the filters, in sort order, ready to render.</summary>
    public IReadOnlyList<LibraryCardView> Cards { get; private set; } = [];

    /// <summary>The titles of the selected media-type tab, before filtering.</summary>
    public int Total { get; private set; }

    /// <summary>The titles of every visible video type, before the tab and the filters.</summary>
    public int LibraryTotal { get; private set; }

    /// <summary>The media-type tabs: the video scopes of this page and the other Library destinations the profile may browse.</summary>
    public IReadOnlyList<LibraryScopeTab> ScopeTabs { get; private set; } = [];

    public LibraryFacets Facets { get; private set; } = new(
        new Dictionary<LibraryProgressState, int>(),
        new Dictionary<LibraryAvailabilityState, int>(),
        0, [], [], [], [], []);

    public LibraryLanguagePreference Preference { get; private set; } = LibraryLanguagePreference.None;

    public IReadOnlyList<CollectionTileView> Collections { get; private set; } = [];

    /// <summary>Supporting details failed to load; the titles are still shown.</summary>
    public bool Degraded { get; private set; }

    public string CurrentHref => LibraryBrowse.Href(Query);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        Query = LibraryBrowse.Parse(key => Request.Query.TryGetValue(key, out var values)
            ? [.. values.Where(x => x is not null).Select(x => x!)]
            : []);

        try
        {
            if (Query.Collections)
            {
                Collections = await collections.ListTilesAsync(User, currentAccount.ProfileId, cancellationToken);
                State = LibraryBrowse.ResolveState(false, false, Collections.Count, Collections.Count);
                return;
            }

            await LoadTitlesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The library page could not be loaded.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            State = LibraryPageState.Error;
        }
    }

    private async Task LoadTitlesAsync(CancellationToken cancellationToken)
    {
        var access = await appShell.GetMediaAccessAsync(User, cancellationToken);
        var visibleVideoTypes = LibraryBrowse.VideoMediaTypes.Where(access.IsVisible).ToArray();
        if (Query.MediaType is { } scope && !visibleVideoTypes.Contains(scope))
        {
            Query = Query with { MediaType = null };
        }

        var otherTabs = UiShellNavigation.BuildLibraryTabs(Request.Path, access.VisibleMediaTypes).Where(tab => tab.Id != UiNavigationCatalog.LibraryVideoTabId);
        ScopeTabs = LibraryBrowse.ScopeTabs(Query, visibleVideoTypes, otherTabs);

        var read = await new LibraryMediaCardQuery(db).GetEntriesAsync(currentAccount.ProfileId, visibleVideoTypes, cancellationToken);

        var degraded = read.Degraded;
        try
        {
            var preferences = await db.ProfilePlaybackPreferences
                .AsNoTracking()
                .Where(x => x.ProfileId == currentAccount.ProfileId)
                .Select(x => new { x.PreferredAudioLanguage, x.PreferredSubtitleLanguage })
                .SingleOrDefaultAsync(cancellationToken);
            if (preferences is not null)
            {
                Preference = LibraryLanguagePreference.From(
                    preferences.PreferredAudioLanguage,
                    preferences.PreferredSubtitleLanguage);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the preferred-language highlight and filter are lost.
            logger.LogWarning(exception, "The playback language preferences could not be read for the library page.");
            degraded = true;
        }

        var scoped = LibraryBrowse.Scope(read.Entries, Query);
        var shown = LibraryBrowse.Apply(scoped, Query, Preference);
        LibraryTotal = read.Entries.Count;
        Total = scoped.Count;
        Facets = LibraryBrowse.Facets([.. scoped], Preference);
        // The play action of a card opens the player; an instance without Playback has none (docs/mockups/instant-play, section 11).
        var playbackEnabled = instanceModules is null || await instanceModules.IsEnabledAsync(InstanceModule.Playback, cancellationToken);
        Cards = [.. shown.Select(entry => LibraryCardView.Create(entry, Preference, Ui)).Select(card => playbackEnabled ? card : card with { Action = null })];
        Degraded = degraded;
        State = LibraryBrowse.ResolveState(false, degraded, Total, Cards.Count);
    }
}
