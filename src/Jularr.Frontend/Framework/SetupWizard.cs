using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Enums.Instance;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Frontend.Framework
{
    // One place of the setup flow: its label, address and the persisted step it belongs to (the Owner is created before any step exists).
    public sealed record SetupPlace(string Key, string LabelKey, string Path, SetupStepType? Step, bool OwnsNext = false);

    // The first-run flow: one step for every topic, in this order; the last one gives the overview and finishes. The stepper at the top is the only navigation.
    public static class SetupWizard
    {
        public const string ProgressKey = "Jularr.SetupProgress";

        private static readonly SetupPlace Owner = new("Owner", "setup.step.Owner", "/Setup", null, true);
        private static readonly SetupPlace Begin = new("Begin", "setup.step.Begin", "/Setup/Begin", SetupStepType.Begin, true);
        private static readonly SetupPlace Storage = new("Storage", "setup.step.Storage", "/Setup/Storage", SetupStepType.Storage);
        private static readonly SetupPlace Takeover = new("Takeover", "setup.step.Takeover", "/Setup/Takeover", SetupStepType.Takeover);
        private static readonly SetupPlace Modules = new("Modules", "setup.step.Modules", "/Setup/Instance", SetupStepType.Modules, true);
        private static readonly SetupPlace Profiles = new("Profiles", "setup.step.Profiles", "/Setup/Profiles", SetupStepType.Profiles);
        private static readonly SetupPlace Libraries = new("Libraries", "setup.step.Libraries", "/Setup/Libraries", SetupStepType.Libraries);
        private static readonly SetupPlace Requests = new("Requests", "setup.step.Requests", "/Setup/Requests", SetupStepType.Requests, true);
        private static readonly SetupPlace DownloadStorage = new("DownloadStorage", "setup.step.DownloadStorage", "/Setup/DownloadStorage", SetupStepType.DownloadStorage);
        private static readonly SetupPlace Downloads = new("Downloads", "setup.step.Downloads", "/Setup/Downloads", SetupStepType.Downloads);
        private static readonly SetupPlace Indexers = new("Indexers", "setup.step.Indexers", "/Setup/Indexers", SetupStepType.Indexers);
        private static readonly SetupPlace Providers = new("Providers", "setup.step.Providers", "/Setup/Providers", SetupStepType.Providers, true);
        private static readonly SetupPlace Overview = new("Overview", "setup.step.Overview", "/Setup/Overview", SetupStepType.Overview, true);

        public static IReadOnlyDictionary<SetupStepType, string> Paths { get; } = new Dictionary<SetupStepType, string>
        {
            [SetupStepType.Begin] = Begin.Path,
            [SetupStepType.Storage] = Storage.Path,
            [SetupStepType.Takeover] = Takeover.Path,
            [SetupStepType.Modules] = Modules.Path,
            [SetupStepType.Profiles] = Profiles.Path,
            [SetupStepType.Libraries] = Libraries.Path,
            [SetupStepType.Requests] = Requests.Path,
            [SetupStepType.DownloadStorage] = DownloadStorage.Path,
            [SetupStepType.Downloads] = Downloads.Path,
            [SetupStepType.Indexers] = Indexers.Path,
            [SetupStepType.Providers] = Providers.Path,
            [SetupStepType.Overview] = Overview.Path
        };

        // The step a check of the backend belongs to; none for what concerns the whole instance.
        public static string? StepOf(string check) => check.Split('.')[0] switch
        {
            "storage" => "Storage",
            "takeover" => "Takeover",
            "modules" => "Modules",
            "requests" => "Requests",
            "tmdb" or "igdb" or "anilist" => "Providers",
            "indexer" => "Indexers",
            "downloader" => "Downloads",
            "library" => "Libraries",
            "downloadStorage" => "DownloadStorage",
            _ => null
        };

        // Something that is there but does not work, as against a step left empty.
        public static bool IsAttention(string check) => check.EndsWith(".unavailable", StringComparison.Ordinal) || check.EndsWith(".failing", StringComparison.Ordinal);

        // The steps still empty (required or not): what the stepper marks as skipped once they are passed.
        public static IReadOnlySet<string> Empty(SetupProgressV1 progress) => progress.Blockers.Concat(progress.Warnings).Where(check => !IsAttention(check))
            .Select(StepOf).OfType<string>().ToHashSet(StringComparer.Ordinal);

        public static bool IsActive(HttpRequest request) => request.Path.StartsWithSegments("/Setup");

        // The address an editor was opened under.
        public static string Address(HttpRequest request, SetupStepType step, string adminPath) => IsActive(request) ? Paths[step] : adminPath;

        // The progress the setup gate read for this request; none before the Owner exists.
        public static SetupProgressV1? Progress(HttpContext context) => context.Items[ProgressKey] as SetupProgressV1;

        // Taking over adds its step; the steps for downloads exist only with Arr+ and the one for requests only with requests.
        public static IReadOnlyList<SetupPlace> Active(SetupProgressV1? progress, UiText texts)
        {
            var known = progress is not null;
            var acquisition = !known || texts.Can(null, "Acquisition");
            var requests = !known || texts.Can(null, "Requests");
            var places = new List<SetupPlace> { Owner, Begin, Storage };
            if (progress?.SetupStartTypeId == SetupStartType.Takeover) places.Add(Takeover);
            places.Add(Modules);
            if (acquisition) places.Add(Profiles);
            places.Add(Libraries);
            if (requests) places.Add(Requests);
            if (acquisition) places.AddRange([DownloadStorage, Downloads, Indexers]);
            places.AddRange([Providers, Overview]);
            return places;
        }

        // Where the flow continues after the last step reached: the first step of the flow from there on.
        public static string Resume(SetupProgressV1 progress, UiText texts)
        {
            var active = Active(progress, texts);
            return (active.FirstOrDefault(place => place.Step is { } step && step >= progress.SetupStepTypeId) ?? active[^1]).Path;
        }

        // The step after the one named, among those that apply.
        public static SetupPlace? After(SetupProgressV1 progress, UiText texts, string key)
        {
            var active = Active(progress, texts);
            var position = active.ToList().FindIndex(item => item.Key == key);
            return position < 0 || position == active.Count - 1 ? null : active[position + 1];
        }

        // The place an address belongs to; the editors of a step (a quality profile, the restore page) belong to it.
        public static SetupPlace? Of(PathString path)
        {
            if (path.Equals("/Setup", StringComparison.OrdinalIgnoreCase)) return Owner;
            if (path.StartsWithSegments("/Setup/Backup")) return Begin;
            return new[] { Begin, Storage, Takeover, Modules, Profiles, Libraries, Requests, DownloadStorage, Downloads, Indexers, Providers, Overview }
                .FirstOrDefault(place => path.StartsWithSegments(place.Path));
        }

        // The steps of the flow at the top of a page, and Back and Next at its foot; the page itself is a standalone one without the app's menus.
        public static void Frame(Page page, HttpContext context)
        {
            page.Shell = false;
            page.NoPageRow();
            if (Of(context.Request.Path) is not { } current) return;
            var texts = context.RequestServices.GetRequiredService<UiText>();
            var assets = context.RequestServices.GetRequiredService<FrontendAssets>();
            var progress = Progress(context);
            var active = Active(progress, texts);
            var position = active.ToList().FindIndex(place => place.Key == current.Key);
            if (position < 0) return;

            // Only the steps already reached can be opened again; before the Owner exists nothing can.
            var reached = progress?.SetupStepTypeId;
            var empty = progress is null ? new HashSet<string>() : Empty(progress);
            var steps = active.Select((place, index) => new StepperStep(place.LabelKey,
                reached is null || place.Step is null || place.Step > reached || index == position ? null : place.Path,
                index == position ? StepState.Current : index < position ? empty.Contains(place.Key) ? StepState.Skipped : StepState.Done : StepState.Upcoming)).ToList();
            page.Content.Insert(0, new Div(new Stepper(steps, "setup.steps", assets)).Class("setup-wizard"));
            page.Asset("Framework/Components/Modal.entry.ts");
            page.Asset("Framework/Components/Truncated.entry.ts");

            // Back and Next; a step with a form of its own continues through that form.
            var foot = new List<IUiComponent>();
            var backPath = context.Request.Path.StartsWithSegments("/Setup/Backup") ? Begin.Path : progress is not null && position > 0 && active[position - 1].Step is not null ? active[position - 1].Path : null;
            if (backPath is not null) foot.Add(new A(new Icon("arrow-left", assets, 15), new Text("setup.back")).Class("button").Attribute("href", backPath));

            if (!current.OwnsNext && progress is not null)
            {
                var csrf = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context).RequestToken;
                foot.Add(new PostForm("/Setup/Progress", csrf, [("place", current.Key)], new Button(new Text("setup.next"), new Icon("arrow-right", assets, 15)).Primary().Submit()).Class("setup-foot__next"));
            }

            if (foot.Count > 0) page.Content.Add(new Div([.. foot]).Class("setup-foot"));
        }
    }
}
