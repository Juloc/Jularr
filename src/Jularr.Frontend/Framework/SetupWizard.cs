using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Enums.Instance;
using Microsoft.AspNetCore.Http;

namespace Jularr.Frontend.Framework
{
    // Admin editors under /Setup.
    public static class SetupWizard
    {
        public static IReadOnlyDictionary<SetupStepType, string> Paths { get; } = new Dictionary<SetupStepType, string>
        {
            [SetupStepType.Instance] = "/Setup/Instance",
            [SetupStepType.Storage] = "/Setup/Storage",
            [SetupStepType.Downloads] = "/Setup/Acquisition",
            [SetupStepType.Providers] = "/Setup/Providers",
            [SetupStepType.Media] = "/Setup/Takeover",
            [SetupStepType.Finish] = "/Setup/Finish"
        };

        public static bool IsActive(HttpRequest request) => request.Path.StartsWithSegments("/Setup");

        // The address an editor was opened under.
        public static string Address(HttpRequest request, SetupStepType step, string adminPath) => IsActive(request) ? Paths[step] : adminPath;

        public static IUiComponent Frame(SetupStepType current, string? csrf)
        {
            var items = new List<IUiComponent>
            {
                new NavigationTabs([.. Paths.Select(step => new NavigationTab("setup.step." + step.Key, step.Value, step.Key == current))], "setup.steps")
            };
            if (current != SetupStepType.Finish)
            {
                items.Add(new Form<UpdateSetupProgressDataV1>(form =>
                {
                    form.Hidden(item => item.SetupStepTypeId);
                    form.Submit("setup.next");
                }, "/Setup/Progress", csrf, new UpdateSetupProgressDataV1(current + 1)));
            }

            return new Div([.. items]).Class("setup-wizard");
        }
    }
}
