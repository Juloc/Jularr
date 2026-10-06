using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Web.Features.Calendar;

/// <param name="Compact">Detail-page rows: the media title and cover are already on the page.</param>
public sealed record ReleaseEventRowModel(
    ReleaseEvent Event,
    ReleaseCalendarPresenter Presenter,
    bool ShowDate,
    bool Compact = false);

public sealed record UpcomingReleasesModel(
    IReadOnlyList<ReleaseEvent> Events,
    ReleaseCalendarPresenter Presenter,
    UiTextBundle Ui,
    string CalendarHref);

/// <summary>
/// The compact "next release / upcoming" block of a media detail page. Renders nothing when no
/// upcoming release is known; the full calendar stays on its own page.
/// </summary>
public sealed class UpcomingReleasesViewComponent(
    ReleaseCalendarService calendar,
    TimeProvider clock,
    IInstanceModuleService modules) : ViewComponent
{
    public const int Limit = 3;

    public async Task<IViewComponentResult> InvokeAsync(ReleaseMediaType mediaType, Guid mediaId, UiTextBundle ui)
    {
        var zone = CalendarTimeZone.Resolve(HttpContext);
        var now = clock.GetUtcNow();
        var events = await calendar.GetUpcomingAsync(mediaType, mediaId, zone, now, Limit, HttpContext.RequestAborted);
        if (events.Count == 0)
        {
            return Content(string.Empty);
        }

        return View(
            "/Pages/Calendar/_UpcomingReleases.cshtml",
            new UpcomingReleasesModel(
                events,
                new ReleaseCalendarPresenter(ui, zone, now, await modules.IsEnabledAsync(InstanceModule.Playback, HttpContext.RequestAborted)),
                ui,
                "/Calendar?type=" + ReleaseCalendarPresenter.FilterValue(mediaType)));
    }
}
