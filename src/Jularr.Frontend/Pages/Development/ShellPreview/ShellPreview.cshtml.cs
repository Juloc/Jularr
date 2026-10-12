using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Activity;
using Jularr.Frontend.Pages.Admin.Library;
using Jularr.Frontend.Pages.Admin.Profiles;
using Jularr.Frontend.Pages.Admin.Requests;
using Jularr.Frontend.Pages.Admin.Rules;
using Jularr.Frontend.Pages.Admin.Users;
using Jularr.Frontend.Pages.Admin.Wanted;
using Jularr.Frontend.Pages.Admin.Work;
using Jularr.Frontend.Pages.Admin.Works;
using Jularr.Frontend.Pages.User.Discover;
using Jularr.Frontend.Pages.User.Details;
using Jularr.Frontend.Pages.User.Player;
using Jularr.Frontend.Pages.User.Settings;
using Jularr.Frontend.Pages.User.Watchlist;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Development.ShellPreview
{
    // Development-only. Uses the actual PageModel definitions; no separate demo screen registry.
    [AllowAnonymous]
    public sealed record PreviewView(string Slug, string LabelKey);

    public sealed class ShellPreviewModel(IHostEnvironment environment) : JularrPage
    {
        public string SelectedView { get; private set; } = "shell";

        public static IReadOnlyList<PreviewView> Views { get; } =
        [
            new("shell", "preview.shell"),
            new("discover", "preview.discover"),
            new("details", "preview.details"),
            new("player", "preview.player"),
            new("watchlist", "preview.watchlist"),
            new("settings", "preview.settings"),
            new("admin-requests", "preview.admin-requests"),
            new("admin-rules", "preview.admin-rules"),
            new("admin-users", "preview.admin-users"),
            new("admin-wanted", "preview.admin-wanted"),
            new("admin-works", "preview.admin-works"),
            new("admin-work", "preview.admin-work"),
            new("admin-library", "preview.admin-library")
        ];

        protected override void Define(Page page)
        {
            page.Asset("Modules/Appearance/appearance.entry.scss");
            JularrPage? source = SelectedView switch
            {
                "discover" => ActivatorUtilities.CreateInstance<IndexModel>(HttpContext.RequestServices),
                "details" => ActivatorUtilities.CreateInstance<DetailsModel>(HttpContext.RequestServices),
                "player" => ActivatorUtilities.CreateInstance<PlayerModel>(HttpContext.RequestServices),
                "watchlist" => ActivatorUtilities.CreateInstance<WatchlistModel>(HttpContext.RequestServices),
                "settings" => ActivatorUtilities.CreateInstance<SettingsModel>(HttpContext.RequestServices),
                "admin-requests" => ActivatorUtilities.CreateInstance<RequestsModel>(HttpContext.RequestServices),
                "admin-rules" => new RulesModel(),
                "admin-users" => ActivatorUtilities.CreateInstance<UsersModel>(HttpContext.RequestServices),
                "admin-wanted" => ActivatorUtilities.CreateInstance<WantedModel>(HttpContext.RequestServices),
                "admin-works" => ActivatorUtilities.CreateInstance<WorksModel>(HttpContext.RequestServices),
                "admin-work" => ActivatorUtilities.CreateInstance<WorkModel>(HttpContext.RequestServices),
                "admin-library" => ActivatorUtilities.CreateInstance<LibraryModel>(HttpContext.RequestServices),
                "admin-activity" => ActivatorUtilities.CreateInstance<ActivityModel>(HttpContext.RequestServices),
                "admin-profiles" => ActivatorUtilities.CreateInstance<ProfilesModel>(HttpContext.RequestServices),
                _ => null
            };

            if (source is not null)
            {
                page.Use(source.BuildDefinition());
            }
            else
            {
                page.Title("shell.preview");
                page.Navigation("nav.home");
            }
        }

        public IActionResult OnGet(string? view)
        {
            if (!environment.IsDevelopment()) return NotFound();
            if (view is not null && !Views.Any(item => item.Slug == view)) return NotFound();

            SelectedView = view ?? "shell";
            PreparePage();
            ViewData["ShellPreview"] = true;
            return Page();
        }
    }
}
