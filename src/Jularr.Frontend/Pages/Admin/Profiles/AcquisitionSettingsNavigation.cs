using System.Globalization;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Http;

namespace Jularr.Frontend.Pages.Admin.Profiles
{
    internal static class AcquisitionSettingsNavigation
    {
        public static readonly (string Id, string Key)[] Panes = [("profiles", "admin.profiles.title"), ("formats", "arr.formats"), ("sizes", "arr.sizes"),
            ("release", "arr.releaseProfiles"), ("tags", "arr.tags")];

        public static string Address(HttpRequest request, string pane, MediaType type) => ProfilesModel.Home(request) + "?tab=" + pane
            + "&mediaTypeId=" + ((byte)type).ToString(CultureInfo.InvariantCulture);

        public static IUiComponent Frame(HttpRequest request, string pane, MediaType type)
        {
            var tabs = new NavigationTabs(Panes.Select(item => new NavigationTab(item.Key, Address(request, item.Id, type), item.Id == pane)).ToArray(), "arr.settings");
            if (pane == "tags") return new Div(tabs).Class("acquisition-settings");
            var scope = new Details(new Summary(new Text("table.type"), new ValueText(": "), new Text(MediaTypeLabel.Key(type, null))),
                new Div(Enum.GetValues<MediaType>().Select(item => (IUiComponent)new A(new Text(MediaTypeLabel.Key(item, null))).Class("button")
                    .Attribute("href", Address(request, pane, item))).ToArray()).Class("acquisition-settings__scope"));
            return new Div(tabs, scope).Class("acquisition-settings");
        }
    }
}
