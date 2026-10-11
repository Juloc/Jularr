namespace Jularr.Frontend.Framework;

internal static class AdminScreens
{
    private static readonly UiSubnav RequestNavigation = new(
    [
        new("admin.requests.title", "/Admin/Requests"),
        new("admin.rules.title", "/Admin/Rules"),
        new("admin.users.title", "/Admin/Requests/Users")
    ]);

    internal static bool Requests(Page page)
    {
        page.Title("admin.requests.title");
        page.Navigation("nav.admin");
        page.SectionTabs("admin.requests");
        page.Content.Add(new UiHeading("admin.requests.title"));
        page.Content.Add(RequestNavigation);
        page.Content.Add(new UiScope(["common.all", "requests.open", "requests.approved",
            "requests.inProgress", "requests.done", "requests.rejected"]));
        page.Content.Add(new UiTable(["table.work", "table.requester", "table.language",
            "table.requestedAt", "table.status", "table.actions"]));
        return true;
    }

    internal static bool Rules(Page page)
    {
        page.Title("admin.rules.title");
        page.Navigation("nav.admin");
        page.SectionTabs("admin.requests");
        page.Content.Add(new UiHeading("admin.rules.title", "admin.rules.description"));
        page.Content.Add(RequestNavigation);
        page.Content.Add(new MasterDetail(md =>
        {
            md.Master.Add(new UiTable(["table.rule", "table.limit"]));
            md.Detail.Add(new UiEmpty());
        }));
        return true;
    }

    internal static bool Users(Page page)
    {
        page.Title("admin.users.title");
        page.Navigation("nav.admin");
        page.SectionTabs("admin.requests");
        page.Content.Add(new UiHeading("admin.users.title", "admin.users.description"));
        page.Content.Add(RequestNavigation);
        page.Content.Add(new MasterDetail(md =>
        {
            md.Master.Add(new UiTable(["table.user", "table.role", "table.rule", "table.limit"]));
            md.Detail.Add(new UiEmpty());
        }));
        return true;
    }

    internal static bool Wanted(Page page)
    {
        page.Title("admin.wanted.title");
        page.Navigation("nav.admin");
        page.Content.Add(new UiHeading("admin.wanted.title", "admin.wanted.description"));
        page.Content.Add(new UiScope(["common.all", "wanted.requested", "wanted.missing",
            "wanted.searching", "wanted.failed"]));
        page.Content.Add(new UiTable(["table.work", "table.type", "table.language",
            "table.profile", "table.status", "table.lastSearch", "table.actions"]));
        page.Content.Add(new UiHint("wanted.dialogDeferred"));
        return true;
    }

    internal static bool Works(Page page)
    {
        page.Title("admin.works.title");
        page.Navigation("nav.admin");
        page.Content.Add(new UiHeading("admin.works.title", "admin.works.description"));
        page.Content.Add(new UiTable(["table.work", "table.type", "table.monitored",
            "table.availability", "table.status", "table.actions"]));
        return true;
    }

    internal static bool Work(Page page)
    {
        page.Title("admin.work.title");
        page.Navigation("nav.admin");
        page.Content.Add(new UiHero("admin.work.title", "admin.work.description"));
        page.Content.Add(new UiHint("admin.work.hierarchy"));
        page.Content.Add(new UiEmpty());
        return true;
    }

    internal static bool Library(Page page)
    {
        page.Title("admin.library.title");
        page.Navigation("nav.admin");
        page.Content.Add(new UiHeading("admin.library.title", "admin.library.description"));
        page.Content.Add(new UiScope(["common.all", "media.anime", "media.series",
            "media.movies", "media.lightNovels", "media.books", "media.manga"]));
        page.Content.Add(new UiTable(["table.work", "table.type", "table.availability",
            "table.size", "table.status", "table.actions"]));
        return true;
    }
}
