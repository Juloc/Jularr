using System.Collections.Frozen;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Profile;

/// <summary>
/// Transitional per-profile notification settings. The full target editor is specified separately;
/// until it lands, this page exposes only the currently real Off/In-App choice while reading and
/// writing the canonical event-preference aggregate.
/// </summary>
public sealed class NotificationsModel(AppDbContext db, NotificationSubscriptionStore subscriptions, CurrentAccountContext account) : PageModel
{
    public const string OffFormValue = "off";
    public const string InAppFormValue = "in-app";

    private static readonly FrozenSet<NotificationChannel> s_inAppOnly = new[] { NotificationChannel.InApp }.ToFrozenSet();

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public IReadOnlyDictionary<JularrEventCategory, NotificationEventPreference> Preferences { get; private set; } = new Dictionary<JularrEventCategory, NotificationEventPreference>();

    public bool IsAdmin { get; private set; }

    public IEnumerable<JularrEventCategory> VisibleCategories => Enum.GetValues<JularrEventCategory>().Where(category => IsAdmin || JularrEventCategories.Of(category).Audience == JularrEventAudience.Profile);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        IsAdmin = account.Can(JularrPolicies.AdminMedia);
        Preferences = await subscriptions.GetAllEventPreferencesAsync(account.ProfileId, cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        IsAdmin = account.Can(JularrPolicies.AdminMedia);

        foreach (var category in VisibleCategories)
        {
            var update = Request.Form[$"delivery.{category}"].ToString() switch
            {
                OffFormValue => new NotificationEventPreferenceUpdate(false, s_inAppOnly, NotificationDeliveryTiming.Immediate),
                InAppFormValue => new NotificationEventPreferenceUpdate(true, s_inAppOnly, NotificationDeliveryTiming.Immediate),
                _ => null
            };

            if (update is not null)
            {
                await subscriptions.SetEventPreferenceAsync(account.ProfileId, category, update, cancellationToken);
            }
        }

        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        TempData["Status"] = Ui["notifications.settings.saved"];
        return RedirectToPage();
    }
}
