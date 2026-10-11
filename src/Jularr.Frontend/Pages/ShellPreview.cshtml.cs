using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages;

// Development-only. Uses the actual PageModel definitions; no separate demo screen registry.
[AllowAnonymous]
public sealed class ShellPreviewModel(IHostEnvironment environment) : JularrPage
{
    public string SelectedView { get; private set; } = "shell";

    public static IReadOnlyList<string> Views { get; } =
    [
        "shell", "discover", "details", "player", "watchlist", "settings",
        "admin-requests", "admin-rules", "admin-users", "admin-wanted",
        "admin-works", "admin-work", "admin-library"
    ];

    protected override void Define(Page page)
    {
        JularrPage? source = SelectedView switch
        {
            "discover" => new IndexModel(),
            "details" => new DetailsModel(),
            "player" => new PlayerModel(),
            "watchlist" => new WatchlistModel(),
            "settings" => new SettingsModel(),
            "admin-requests" => new Admin.RequestsModel(),
            "admin-rules" => new Admin.RulesModel(),
            "admin-users" => new Admin.UsersModel(),
            "admin-wanted" => new Admin.WantedModel(),
            "admin-works" => new Admin.WorksModel(),
            "admin-work" => new Admin.WorkModel(),
            "admin-library" => new Admin.LibraryModel(),
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
            page.Content.Add(new UiHeading("shell.preview", "shell.previewInfo"));
        }
    }

    public IActionResult OnGet(string? view)
    {
        if (!environment.IsDevelopment()) return NotFound();
        if (view is not null && !Views.Contains(view, StringComparer.Ordinal)) return NotFound();

        SelectedView = view ?? "shell";
        PreparePage();
        ViewData["ShellPreview"] = true;
        return Page();
    }
}
