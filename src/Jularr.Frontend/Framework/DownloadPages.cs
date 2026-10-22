using System.Globalization;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Frontend.Framework
{
    // The panes of the quality rules share one page and name their pane in the address, scoped by a media type (tags belong to every type).
    public static class DownloadPages
    {
        public const string Profiles = "profiles";
        public const string Formats = "formats";
        public const string Sizes = "sizes";
        public const string Release = "release";
        public const string Tags = "tags";

        public const string ProfilesAddress = "/Admin/Acquisition/Profiles";

        public static readonly string[] Panes = [Profiles, Formats, Sizes, Release, Tags];

        public static string PaneAddress(string pane, MediaType type, string home = ProfilesAddress) =>
            home + "?tab=" + pane + "&mediaTypeId=" + ((byte)type).ToString(CultureInfo.InvariantCulture);

        // The setup wizard shows only the quality rules, at its own address; the administration's row of its entry comes from AdminMenu.
        public static NavigationTabs SetupTabs(string current, MediaType scope, string profilesHome) => new(
        [
            new("admin.profiles.title", PaneAddress(Profiles, scope, profilesHome), current == Profiles),
            new("arr.formats", PaneAddress(Formats, scope, profilesHome), current == Formats),
            new("arr.sizes", PaneAddress(Sizes, scope, profilesHome), current == Sizes),
            new("arr.releaseProfiles", PaneAddress(Release, scope, profilesHome), current == Release),
            new("arr.tags", PaneAddress(Tags, scope, profilesHome), current == Tags)
        ], "downloads.sections", "filters");
    }
}
