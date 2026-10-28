using System.Text.Json;
using Jularr.Backend.Data.Admin.Instance.V1;

namespace Jularr.Frontend.Framework
{
    // The card of one module, as the Modules page and the setup's module step show it: its icon, name and state, its switch, and the modules it needs
    // and the ones that need it (Modules.entry.ts keeps those tags and the switch's freedom to move as the switches change).
    public static class ModuleCard
    {
        // The icon of what a module does, from the project's icon set.
        private static readonly Dictionary<string, string> Icons = new()
        {
            ["Library"] = "library",
            ["Requests"] = "requests",
            ["Acquisition"] = "download",
            ["Browser"] = "globe",
            ["Vpn"] = "shield-check",
            ["Torrent"] = "swap",
            ["Playback"] = "play",
            ["Reading"] = "book",
            ["Profiles"] = "users",
            ["Emulation"] = "gamepad",
            ["Notifications"] = "bell",
            ["Collections"] = "bookmark",
            ["Calendar"] = "calendar",
            ["Statistics"] = "pulse",
            ["UnitGroups"] = "list",
            ["HomeLayout"] = "home",
            ["Trickplay"] = "scan",
            ["Franchises"] = "layers",
            ["Diagnostics"] = "activity",
            ["Subtitles"] = "subtitles",
            ["Uploads"] = "upload",
            ["ProgressSync"] = "sync",
            ["AniList"] = "anilist",
            ["Devices"] = "devices",
            ["Registration"] = "user-plus",
            ["Translation"] = "globe",
            ["Ocr"] = "text-select",
            ["SmartPdf"] = "document",
            ["DiscoverCache"] = "drive",
            ["LocalDiscovery"] = "network"
        };

        public static IUiComponent Build(InstanceModuleDetailsV1 module, IReadOnlyList<InstanceModuleDetailsV1> modules, IUiComponent control, bool isOn,
            IReadOnlyDictionary<string, bool> enabled, FrontendAssets assets, UiText texts)
        {
            var tags = new List<IUiComponent>();
            if (module.Requires.Length > 0)
            {
                tags.Add(new Span(new Text("settings.module.needs")).Class("setting-card__tags-label"));
                tags.AddRange(module.Requires.Select(key => (IUiComponent)new Tag(new Truncated(texts["instance.module." + key])).Attribute("data-needs", key)
                    .Class(enabled.GetValueOrDefault(key) ? "tag" : "tag tag--warning")));
            }

            // Those that need this module and are on, in one line; the script keeps it as they are switched.
            var dependants = modules.Where(other => other.Requires.Contains(module.Key)).ToList();
            if (dependants.Count > 0)
            {
                var names = string.Join(", ", dependants.Where(other => enabled.GetValueOrDefault(other.Key)).Select(other => texts["instance.module." + other.Key]));
                var needed = new Tag(new Span(new Text("settings.module.neededBy")).Class("setting-card__tags-label"), new Truncated(names)).Attribute("data-needed-names",
                    JsonSerializer.Serialize(dependants.ToDictionary(other => other.Key, other => texts["instance.module." + other.Key])));
                tags.Add(names.Length > 0 ? needed : needed.Attribute("hidden", "hidden"));
            }

            var blocked = isOn ? dependants.Any(other => enabled.GetValueOrDefault(other.Key)) : module.Requires.Any(key => !enabled.GetValueOrDefault(key));
            var card = new SettingCard(new IconTile(new Icon(Icons.GetValueOrDefault(module.Key, "settings"), assets, 17)), new Truncated(texts["instance.module." + module.Key]), control, isOn,
                "admin.providers.enabled", "admin.providers.disabled", tags).Data("module", module.Key).Data("requires", string.Join(' ', module.Requires));
            return blocked ? card.Blocked() : card;
        }
    }
}
