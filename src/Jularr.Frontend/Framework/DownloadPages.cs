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
        public const string Groups = "groups";
        public const string Tags = "tags";

        public const string ProfilesAddress = "/Admin/Acquisition/Profiles";

        public static readonly string[] Panes = [Profiles, Formats, Sizes, Release, Groups, Tags];

        public static string PaneAddress(string pane, MediaType type, string home = ProfilesAddress) =>
            home + "?tab=" + pane + "&mediaTypeId=" + ((byte)type).ToString(CultureInfo.InvariantCulture);

    }
}
