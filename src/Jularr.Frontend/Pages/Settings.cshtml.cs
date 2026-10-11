using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages;

public sealed class SettingsModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("settings.title");
        page.Navigation("nav.settings");
        page.Asset("Pages/Settings.entry.scss");
        page.Content.Add(new UiSection("settings.general", [
            new ItemList("settings.appearance", "settings.language", "settings.accessibility")
        ]));
        page.Content.Add(new UiSection("settings.media", [
            new ItemList("settings.library", "settings.playback", "settings.audioSubtitles",
                "settings.reader", "settings.ratings")
        ]));
        page.Content.Add(new UiSection("settings.personal", [
            new ItemList("settings.modules", "settings.notifications", "settings.connections")
        ]));
        page.Content.Add(new UiSection("settings.account", [
            new ItemList("settings.sessions", "settings.privacy", "settings.security")
        ]));
    }
}
