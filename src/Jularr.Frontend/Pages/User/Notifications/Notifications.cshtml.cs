using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Notification;
using Jularr.Backend.Data.User.Notification.V1;
using Jularr.Backend.Service.User.Notification.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.User.Notifications
{
    // The profile's inbox and which kinds of notices it wants.
    public sealed class NotificationsModel(ReadNotificationsV1 read, MarkNotificationsReadV1 markRead, ReadNotificationSettingsV1 readSettings, UpdateNotificationSettingV1 updateSetting,
        IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private PageResult<NotificationV1>? _notifications;
        private IReadOnlyList<NotificationSettingV1> _settings = [];

        [BindProperty(SupportsGet = true)]
        public int PageNumber { get; set; } = 1;

        protected override void Define(Page page)
        {
            page.Title("notifications.title");
            page.Asset("Pages/User/Notifications/Notifications.entry.scss");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            if (_notifications is not { } notifications) return;

            if (texts.UnreadNotifications > 0)
            {
                page.Content.Add(new Form<NotificationsParametersV1>(form => form.Submit("notifications.markRead"), "/Notifications?handler=Read", csrf, new NotificationsParametersV1()));
            }

            if (notifications.Items.Count == 0)
            {
                page.Content.Add(new P(new Text("notifications.empty")).Attribute("role", "status"));
            }

            page.Content.Add(new Ul([.. notifications.Items.Select(Item)]).Class("notifications"));
            var paging = new List<IUiComponent>();
            if (notifications.Page > 1) paging.Add(new A(new Text("common.previous")).Class("button").Attribute("href", "/Notifications?pageNumber=" + (notifications.Page - 1).ToString(CultureInfo.InvariantCulture)));
            if (notifications.Items.Count == notifications.PageSize) paging.Add(new A(new Text("common.next")).Class("button").Attribute("href", "/Notifications?pageNumber=" + (notifications.Page + 1).ToString(CultureInfo.InvariantCulture)));
            if (paging.Count > 0) page.Content.Add(new Nav([.. paging]).Class("notifications__paging").Attribute("aria-label", texts["notifications.pages"]));

            // One switch per kind of notice.
            var states = new[] { new SelectOption("False", texts["notifications.on"]), new SelectOption("True", texts["notifications.off"]) };
            page.Content.Add(new UiSection("notifications.settings", [.. _settings.Select(setting => (IUiComponent)new Form<UpdateNotificationSettingDataV1>(form =>
            {
                form.Hidden(item => item.NotificationTypeId);
                form.Select(item => item.IsMuted, "notifications.type." + setting.NotificationTypeId, states);
                form.Submit("common.save");
            }, "/Notifications?handler=Setting", csrf, new UpdateNotificationSettingDataV1(setting.NotificationTypeId, setting.IsMuted)))]));
        }

        private IUiComponent Item(NotificationV1 item)
        {
            var text = string.Format(CultureInfo.CurrentCulture, texts["notifications.message." + item.NotificationTypeId], item.Subject);
            if (item.Occurrences > 1) text += " " + string.Format(CultureInfo.CurrentCulture, texts["notifications.times"], item.Occurrences);
            IUiComponent message = item.Link is { } link ? new A(new ValueText(text)).Attribute("href", link) : new Span(new ValueText(text));
            return new Li(message, new Span(new ValueText(item.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))).Class("notifications__time"))
                .Class("notifications__item" + (item.IsRead ? "" : " notifications__item--unread"));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            var token = HttpContext.RequestAborted;
            _notifications = await read.ExecuteAsync<PageResult<NotificationV1>>(new ReadNotificationsParametersV1(Math.Max(1, PageNumber)), token);
            _settings = await readSettings.ExecuteAsync<BaseDataList<NotificationSettingV1>>(new NotificationsParametersV1(), token);
        }

        public async Task<IActionResult> OnPostReadAsync()
        {
            await markRead.ExecuteAsync<UpdateResult>(new NotificationsParametersV1(), default, HttpContext.RequestAborted);
            return LocalRedirect("/Notifications");
        }

        public async Task<IActionResult> OnPostSettingAsync([BindRequired] NotificationType notificationTypeId, [BindRequired] bool isMuted)
        {
            await updateSetting.ExecuteAsync<UpdateResult>(new NotificationsParametersV1(), new UpdateNotificationSettingDataV1(notificationTypeId, isMuted), HttpContext.RequestAborted);
            return LocalRedirect("/Notifications");
        }
    }
}
