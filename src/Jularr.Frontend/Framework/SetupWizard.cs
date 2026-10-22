using Jularr.Backend.Data.Enums.Instance;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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

        // The step row of the flow; the Owner step (before anyone is signed in) is the one with no step type.
        public static IUiComponent Frame(HttpContext context, SetupStepType? current, string? sourcePane = null, bool acquisition = true)
        {
            var isSource = current >= SetupStepType.Indexers && current != SetupStepType.DownloadStorage;
            var places = new List<(string Label, string Path, int Position)>
            {
                ("setup.step.Owner", "/Setup", 0),
                ("setup.step.Instance", "/Setup/Instance", 1),
                ("setup.step.Storage", "/Setup/Storage", 2)
            };
            if (acquisition) places.Add(("downloadStorage.title", Paths[SetupStepType.DownloadStorage], 3));
            places.Add(("setup.step.Sources", "/Setup/Sources", 4));
            var position = current switch
            {
                null => 0,
                SetupStepType.Instance => 1,
                SetupStepType.Storage => 2,
                SetupStepType.DownloadStorage => 3,
                _ => 4
            };

            // Nothing past the Owner step can be opened before the Owner exists.
            var steps = places.Select(place => new StepperStep(place.Label, current is null ? null : place.Path,
                place.Position == position ? StepState.Current : place.Position < position ? StepState.Done : StepState.Upcoming)).ToList();
            var items = new List<IUiComponent> { new Stepper(steps, "setup.steps", context.RequestServices.GetRequiredService<FrontendAssets>()) };
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
