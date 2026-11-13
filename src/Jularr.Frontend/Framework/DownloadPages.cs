using Microsoft.AspNetCore.Http;

namespace Jularr.Frontend.Framework
{
    // The quality rules share one page in three tabs, as Sonarr has them: profiles, custom formats (with release profiles, release groups and tags as sections) and
    // quality sizes. No tab is about one media type.
    public static class DownloadPages
    {
        public const string Profiles = "profiles";
        public const string Formats = "formats";
        public const string Groups = "groups";
        public const string Releases = "releases";
        public const string Quality = "quality";

        public const string ProfilesAddress = "/Admin/Acquisition/Profiles";

        public static readonly string[] Panes = [Profiles, Formats, Groups, Releases, Quality];

        // The tab an address names; an unknown one is the profiles.
        public static string Current(string? tab) => Panes.FirstOrDefault(pane => string.Equals(pane, tab?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Profiles;

        // Where an address of the former panes (`?tab=sizes|release|tags`) or of the media-type bar (`mediaTypeId`, `subtype`) leads now; null when it is current.
        public static string? Legacy(IQueryCollection query, string home = ProfilesAddress)
        {
            var tab = query["tab"].ToString().Trim().ToLowerInvariant();
            var moved = tab switch
            {
                "sizes" => Quality,
                "release" or "tags" => Releases,
                _ => null
            };
            if (moved is null && !query.ContainsKey("mediaTypeId") && !query.ContainsKey("subtype")) return null;

            var target = moved ?? Current(tab);
            var anchor = tab switch { "tags" => "#tags", _ => "" };
            return home + (target == Profiles ? "" : "?tab=" + target) + anchor;
        }
    }
}
