namespace Jularr.Frontend.Framework
{
    // The settings of the instance are pages of their own; this is their one row of navigation, each page by its permission and modules.
    public static class SettingsPages
    {
        public const string Instance = "instance";
        public const string Modules = "modules";
        public const string MediaTypes = "mediaTypes";
        public const string Languages = "languages";
        public const string Naming = "naming";

        public static NavigationTabs Tabs(UiText texts, string current)
        {
            var tabs = new List<NavigationTab>();
            void Add(string id, string key, string href, bool allowed)
            {
                if (allowed) tabs.Add(new NavigationTab(key, href, id == current));
            }

            var administers = texts.CanAdminister();
            Add(Instance, "settings.tab.instance", "/Admin/Instance", administers);
            Add(Modules, "settings.tab.modules", "/Admin/Instance/Modules", administers);
            Add(MediaTypes, "settings.tab.mediaTypes", "/Admin/Instance/MediaTypes", administers);
            Add(Languages, "settings.tab.languages", "/Admin/Instance/Languages", texts.Can("localization.read"));
            Add(Naming, "settings.tab.naming", "/Admin/Instance/Naming", texts.Can("storage.read", "Library"));
            return new NavigationTabs(tabs, "settings.tabs");
        }
    }
}
