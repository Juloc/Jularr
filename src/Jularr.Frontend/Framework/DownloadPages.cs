using System.Globalization;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Frontend.Framework
{
    // The pages of the acquisition settings (indexers, download clients, subtitles and the quality rules) are pages of their own; this is their one row of navigation.
    public static class DownloadPages
    {
        public const string Indexers = "indexers";
        public const string Clients = "clients";
        public const string Subtitles = "subtitles";
        public const string Profiles = "profiles";
        public const string Formats = "formats";
        public const string Sizes = "sizes";
        public const string Release = "release";
        public const string Tags = "tags";

        // The quality rules share one page and name their pane in the address, scoped by a media type (tags belong to every type).
        public const string ProfilesAddress = "/Admin/Acquisition/Profiles";

        public static readonly string[] Panes = [Profiles, Formats, Sizes, Release, Tags];

        public static string PaneAddress(string pane, MediaType type, string home = ProfilesAddress) =>
            home + "?tab=" + pane + "&mediaTypeId=" + ((byte)type).ToString(CultureInfo.InvariantCulture);

        // The setup wizard shows only the quality rules, at its own address.
        public static NavigationTabs Tabs(string current, MediaType scope = MediaType.Movie, string profilesHome = ProfilesAddress, bool panesOnly = false)
        {
            var tabs = new List<NavigationTab>();
            if (!panesOnly)
            {
                tabs.Add(new("acquisition.role.Search", "/Admin/Acquisition", current == Indexers));
                tabs.Add(new("acquisition.role.Download", "/Admin/Acquisition/Downloads", current == Clients));
                tabs.Add(new("acquisition.role.Subtitles", "/Admin/Acquisition/Subtitles", current == Subtitles));
            }

            tabs.Add(new("admin.profiles.title", PaneAddress(Profiles, scope, profilesHome), current == Profiles));
            tabs.Add(new("arr.formats", PaneAddress(Formats, scope, profilesHome), current == Formats));
            tabs.Add(new("arr.sizes", PaneAddress(Sizes, scope, profilesHome), current == Sizes));
            tabs.Add(new("arr.releaseProfiles", PaneAddress(Release, scope, profilesHome), current == Release));
            tabs.Add(new("arr.tags", PaneAddress(Tags, scope, profilesHome), current == Tags));
            return new NavigationTabs(tabs, "downloads.sections", "filters");
        }
    }
}
