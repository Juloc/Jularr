namespace Jularr.Frontend.Localization;

// Presentation fallbacks only. Translation descriptions and AI context are stored in the backend catalog.
internal static class AdminSources
{
    internal static IReadOnlyDictionary<string, string> English { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["admin.requests.title"] = "Requests",
        ["admin.rules.title"] = "Rules",
        ["admin.users.title"] = "Users",
        ["admin.rules.description"] = "Approval and request rules",
        ["admin.users.description"] = "Account assignments and request permissions",
        ["admin.wanted.title"] = "Wanted",
        ["admin.wanted.description"] = "Acquisition needs after request approval",
        ["admin.works.title"] = "Works",
        ["admin.works.description"] = "Canonical media and monitoring",
        ["admin.work.title"] = "Work",
        ["admin.work.description"] = "Canonical Work details, versions and monitoring",
        ["admin.work.hierarchy"] = "Work → Structure → Edition → Version → Asset → File",
        ["admin.library.title"] = "Admin library",
        ["admin.library.description"] = "Imported media and file availability",
        ["requests.open"] = "Open",
        ["requests.approved"] = "Approved",
        ["requests.inProgress"] = "In progress",
        ["requests.done"] = "Done",
        ["requests.rejected"] = "Rejected",
        ["wanted.requested"] = "Requested",
        ["wanted.missing"] = "Missing",
        ["wanted.searching"] = "Searching",
        ["wanted.failed"] = "Failed",
        ["wanted.dialogDeferred"] = "Search, Current and History will be added when the acquisition services exist.",
        ["table.work"] = "Work / unit",
        ["table.requester"] = "Requester",
        ["table.language"] = "Language / version",
        ["table.requestedAt"] = "Requested at",
        ["table.status"] = "Status",
        ["table.actions"] = "Actions",
        ["table.rule"] = "Rule",
        ["table.limit"] = "Limit",
        ["table.user"] = "User",
        ["table.role"] = "Role",
        ["table.type"] = "Type",
        ["table.profile"] = "Profile",
        ["table.lastSearch"] = "Last search",
        ["table.monitored"] = "Monitoring",
        ["table.availability"] = "Availability",
        ["table.size"] = "Storage used",
    };
}
