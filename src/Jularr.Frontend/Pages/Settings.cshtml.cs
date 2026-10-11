using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages;

public sealed class SettingsModel : PreparedPage
{
    protected override void Define(Page page)
    {
        page.Title("settings.title");
        page.Asset("Pages/Settings.entry.scss");
        page.Navigation("nav.settings");
        page.Content.Add(new UiSettingsGroup("settings.general", [
            new("settings.appearance"), new("settings.language"), new("settings.accessibility")
        ]));
        page.Content.Add(new UiSettingsGroup("settings.media", [
            new("settings.library"), new("settings.playback"), new("settings.audioSubtitles"),
            new("settings.reader"), new("settings.ratings")
        ]));
        page.Content.Add(new UiSettingsGroup("settings.personal", [
            new("settings.modules"), new("settings.notifications"), new("settings.connections")
        ]));
        page.Content.Add(new UiSettingsGroup("settings.account", [
            new("settings.sessions"), new("settings.privacy"), new("settings.security")
        ]));
    }
}
