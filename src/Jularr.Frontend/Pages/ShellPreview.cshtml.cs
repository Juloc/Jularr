using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages;

[AllowAnonymous]
public sealed class ShellPreviewModel(IHostEnvironment environment) : JularrPage
{
    protected override void Define(Page page)
    {
        page.Navigation("nav.home");
        page.Content.Add(new Panel(panel =>
        {
            panel.Add(new PreviewText("shell.previewInfo"));
        }));
    }

    public IActionResult OnGet()
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        PreparePage();
        ViewData["ShellPreview"] = true;
        return Page();
    }
}

public sealed record PreviewText(string Key) : IUiComponent;
