using Jularr.Web.Data;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Web.Pages.Settings;

/// <summary>
/// Settings → Offline (#221 part 1): storage usage, Wi-Fi-only toggle and
/// per-book management of downloaded content. Everything on this page reads
/// browser-local state (IndexedDB/OPFS, wwwroot/js/offline-library*.js); the
/// server side only renders the localized shell.
/// </summary>
public sealed class OfflineModel(
    AppDbContext db,
    IInstanceModuleService? instanceModules = null) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;
    public IReadOnlyList<OfflineMediaEntry> Media { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var instance = instanceModules is null
            ? InstanceModuleSettings.Default
            : await instanceModules.GetAsync(cancellationToken);
        var entries = new List<OfflineMediaEntry>();

        // Offline copies of video and audio are a playback feature (their package API is part of that module); a manager-only instance lists none.
        var playback = instance.IsEnabled(InstanceModule.Playback);
        if (playback && instance.IsEnabled(InstanceModule.Anime))
        {
            entries.AddRange(await db.Episodes.AsNoTracking().OrderBy(x => x.DiscoveredAt)
                .Select(x => new OfflineMediaEntry("episode", x.Id, x.Title)).ToListAsync(cancellationToken));
        }
        if (playback && instance.IsEnabled(InstanceModule.Movie))
        {
            entries.AddRange(await db.Movies.AsNoTracking().OrderBy(x => x.Title)
                .Select(x => new OfflineMediaEntry("movie", x.Id, x.Title)).ToListAsync(cancellationToken));
        }
        if (playback && instance.IsEnabled(InstanceModule.Tv))
        {
            entries.AddRange(await db.TvSeries.AsNoTracking().OrderBy(x => x.Title)
                .Select(x => new OfflineMediaEntry("tv", x.Id, x.Title)).ToListAsync(cancellationToken));
        }
        if (playback && instance.IsEnabled(InstanceModule.Audiobook))
        {
            entries.AddRange(await db.Audiobooks.AsNoTracking().OrderBy(x => x.Title)
                .Select(x => new OfflineMediaEntry("audiobook", x.Id, x.Title)).ToListAsync(cancellationToken));
        }
        if (instance.IsEnabled(InstanceModule.Book))
        {
            entries.AddRange(await db.BookEditions.AsNoTracking().OrderBy(x => x.Title)
                .Select(x => new OfflineMediaEntry("book", x.WorkId, x.Title ?? "Book")).Distinct().ToListAsync(cancellationToken));
        }

        Media = entries;
    }
}

public sealed record OfflineMediaEntry(string Kind, Guid Id, string Title);
