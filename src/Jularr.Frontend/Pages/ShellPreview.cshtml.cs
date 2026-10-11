using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages;

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
        if (SelectedView != "shell" && Screens.Populate(SelectedView, page))
        {
            return;
        }

        page.Navigation("nav.home");
        page.Title("shell.preview");
        page.Content.Add(new Panel(panel => panel.Add(new PreviewText("shell.previewInfo"))));
    }

    public IActionResult OnGet(string? view)
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        if (view is not null && !Views.Contains(view, StringComparer.Ordinal))
        {
            return NotFound();
        }

        SelectedView = view ?? "shell";
        PreparePage();
        ViewData["ShellPreview"] = true;
        return Page();
    }
}

public sealed record PreviewText(string Key) : IUiComponent;
