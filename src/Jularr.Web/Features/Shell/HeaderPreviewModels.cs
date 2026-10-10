using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Notifications;

namespace Jularr.Web.Features.Shell;

public sealed record NotificationPreview(UiTextBundle Ui, IReadOnlyList<NotificationItem> Items, int Unread);
