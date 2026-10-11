namespace Jularr.Frontend.Framework;

internal static class UserScreens
{
    internal static bool Discover(Page page)
    {
        page.Title("discover.title");
        page.Navigation("nav.home");
        page.Content.Add(new UiHeading("discover.title"));
        page.Content.Add(new UiScope(["media.all", "media.anime", "media.series", "media.movies",
            "media.lightNovels", "media.books", "media.manga"]));
        page.Content.Add(new UiHero("discover.hero", "discover.heroDescription"));
        page.Content.Add(new UiHeading("discover.continue"));
        page.Content.Add(new UiEmpty());
        page.Content.Add(new UiHeading("discover.trending"));
        page.Content.Add(new UiEmpty());
        return true;
    }

    internal static bool Details(Page page)
    {
        page.Title("details.title");
        page.Navigation("nav.home");
        page.Content.Add(new UiHero("details.title", "details.heroDescription"));
        // Season/episode navigation and versions depend on canonical media type and Work data.
        page.Content.Add(new UiMediaColumns());
        page.Content.Add(new UiEmpty());
        return true;
    }

    internal static bool Player(Page page)
    {
        page.Title("player.title");
        page.Shell = false;
        page.Content.Add(new UiPlayer());
        return true;
    }

    internal static bool Watchlist(Page page)
    {
        page.Title("watchlist.title");
        page.Navigation("nav.watchlist");
        page.Content.Add(new UiHeading("watchlist.title"));
        page.Content.Add(new UiScope(["media.all", "media.anime", "media.series",
            "media.movies", "media.lightNovels", "media.books", "media.manga"]));
        page.Content.Add(new UiEmpty());
        return true;
    }

    internal static bool Settings(Page page)
    {
        page.Title("settings.title");
        page.Navigation("nav.profile");
        page.Content.Add(new UiHeading("settings.title", "settings.profileNotice"));
        page.Content.Add(new UiGroup("settings.general",
            ["settings.appearance", "settings.language", "settings.accessibility"]));
        page.Content.Add(new UiGroup("settings.media",
            ["settings.library", "settings.playback", "settings.audioSubtitles", "settings.reader", "settings.ratings"]));
        page.Content.Add(new UiGroup("settings.personal",
            ["settings.modules", "settings.notifications", "settings.connections"]));
        page.Content.Add(new UiGroup("settings.account",
            ["settings.sessions", "settings.privacy", "settings.security"]));
        return true;
    }
}
