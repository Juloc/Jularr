using Jularr.Frontend.Framework;
using Jularr.Frontend.Pages.Admin.Library;
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
            JularrPage? source = SelectedView switch
            {
                "discover" => new IndexModel(),
                "details" => new DetailsModel(),
                "player" => new PlayerModel(),
                "watchlist" => new WatchlistModel(),
                "settings" => ActivatorUtilities.CreateInstance<SettingsModel>(HttpContext.RequestServices),
                "admin-requests" => ActivatorUtilities.CreateInstance<RequestsModel>(HttpContext.RequestServices),
                "admin-rules" => new RulesModel(),
                "admin-users" => new UsersModel(),
                "admin-wanted" => new WantedModel(),
                "admin-works" => new WorksModel(),
                "admin-work" => new WorkModel(),
                "admin-library" => new LibraryModel(),
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
