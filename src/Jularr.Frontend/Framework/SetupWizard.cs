using Jularr.Backend.Data.Enums.Instance;
using Microsoft.AspNetCore.Http;

namespace Jularr.Frontend.Framework
{
    // Download storage follows library storage when acquisition is enabled.
    public static class SetupWizard
    {
        public static IReadOnlyDictionary<SetupStepType, string> Paths { get; } = new Dictionary<SetupStepType, string>
        {
            [SetupStepType.Instance] = "/Setup/Instance",
            [SetupStepType.Storage] = "/Setup/Storage",
            [SetupStepType.DownloadStorage] = "/Setup/DownloadStorage",
            [SetupStepType.Indexers] = "/Setup/Indexers",
            [SetupStepType.Downloads] = "/Setup/Downloads",
            [SetupStepType.Providers] = "/Setup/Providers",
            [SetupStepType.Profiles] = "/Setup/Profiles",
            [SetupStepType.Media] = "/Setup/Takeover"
        };

        public static bool IsActive(HttpRequest request) => request.Path.StartsWithSegments("/Setup");

        public static string ResumePath(SetupStepType step) => step switch
        {
            SetupStepType.Instance => "/Setup/Instance",
            SetupStepType.Storage => "/Setup/Storage",
            SetupStepType.DownloadStorage => "/Setup/DownloadStorage",
            _ => "/Setup/Sources"
        };

        // The address an editor was opened under.
        public static string Address(HttpRequest request, SetupStepType step, string adminPath) => IsActive(request) ? Paths[step] : adminPath;

        public static IUiComponent Frame(SetupStepType current, string? sourcePane = null, bool acquisition = true)
        {
            var isSource = current >= SetupStepType.Indexers && current != SetupStepType.DownloadStorage;
            var steps = new List<NavigationTab>
            {
                new("setup.step.Owner", "/Setup", false),
                new("setup.step.Instance", "/Setup/Instance", current == SetupStepType.Instance),
                new("setup.step.Storage", "/Setup/Storage", current == SetupStepType.Storage)
            };
            if (acquisition) steps.Add(new("downloadStorage.title", Paths[SetupStepType.DownloadStorage], current == SetupStepType.DownloadStorage));
            steps.Add(new("setup.step.Sources", "/Setup/Sources", isSource));
            var items = new List<IUiComponent> { new NavigationTabs(steps, "setup.steps") };
            if (current == SetupStepType.Instance)
            {
                items.Add(new A(new Text("setup.restore")).Class("button").Attribute("href", "/Setup/Backup"));
            }

            if (isSource)
            {
                var selected = sourcePane ?? current switch
                {
                    SetupStepType.Indexers => "indexers",
                    SetupStepType.Downloads => "downloads",
                    SetupStepType.Profiles => "profiles",
                    SetupStepType.Media => "takeover",
                    _ => "providers"
                };
                var tabs = new List<NavigationTab> { new("setup.sources.metadata", "/Setup/Sources", selected == "metadata") };
                if (acquisition)
                {
                    tabs.AddRange([
                        new("setup.step.Indexers", Paths[SetupStepType.Indexers], selected == "indexers"),
                        new("setup.step.Downloads", Paths[SetupStepType.Downloads], selected == "downloads"),
                        new("setup.step.Profiles", Paths[SetupStepType.Profiles], selected == "profiles"),
                        new("admin.takeover.title", Paths[SetupStepType.Media], selected == "takeover")
                    ]);
                }
                tabs.Add(new("admin.providers.title", Paths[SetupStepType.Providers], selected == "providers"));
                items.Add(new NavigationTabs(tabs, "setup.step.Sources"));
            }

            return new Div([.. items]).Class("setup-wizard");
        }
    }
}
