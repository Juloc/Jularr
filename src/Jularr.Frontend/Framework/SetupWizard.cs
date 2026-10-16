using Jularr.Backend.Data.Enums.Instance;
using Microsoft.AspNetCore.Http;

namespace Jularr.Frontend.Framework
{
    // Four main steps; the ordinary editors remain auxiliary routes of their step.
    public static class SetupWizard
    {
        public static IReadOnlyDictionary<SetupStepType, string> Paths { get; } = new Dictionary<SetupStepType, string>
        {
            [SetupStepType.Instance] = "/Setup/Instance",
            [SetupStepType.Storage] = "/Setup/Storage",
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
            _ => "/Setup/Sources"
        };

        // The address an editor was opened under.
        public static string Address(HttpRequest request, SetupStepType step, string adminPath) => IsActive(request) ? Paths[step] : adminPath;

        public static IUiComponent Frame(SetupStepType current, bool showReturn = true)
        {
            var items = new List<IUiComponent>
            {
                new NavigationTabs([
                    new NavigationTab("setup.step.Owner", "/Setup", false),
                    new NavigationTab("setup.step.Instance", "/Setup/Instance", current == SetupStepType.Instance),
                    new NavigationTab("setup.step.Storage", "/Setup/Storage", current == SetupStepType.Storage),
                    new NavigationTab("setup.step.Sources", "/Setup/Sources", current >= SetupStepType.Indexers)
                ], "setup.steps")
            };
            if (current == SetupStepType.Instance)
            {
                items.Add(new A(new Text("setup.restore")).Class("button").Attribute("href", "/Setup/Backup"));
            }

            if (current >= SetupStepType.Indexers && showReturn)
            {
                items.Add(new A(new Text("setup.sources.back")).Class("button").Attribute("href", "/Setup/Sources"));
            }

            return new Div([.. items]).Class("setup-wizard");
        }
    }
}
