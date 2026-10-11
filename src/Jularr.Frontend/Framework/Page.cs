using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Frontend.Framework;

public abstract class JularrPage : PageModel
{
    public Page Definition { get; private set; } = new();

    protected abstract void Define(Page page);

    protected void PreparePage() => Definition = BuildDefinition();

    internal Page BuildDefinition()
    {
        var page = new Page();
        Define(page);
        return page;
    }
}

public abstract class PreparedPage : JularrPage
{
    public IActionResult OnGet()
    {
        PreparePage();
        return Page();
    }
}

public sealed class Page
{
    public bool Shell { get; set; } = true;
    public bool IsPlayer { get; set; }
    public string? TitleKey { get; private set; }
    public string? NavigationKey { get; private set; }
    public List<IUiComponent> Content { get; } = [];

    public void Title(string key) => TitleKey = key;
    public void Navigation(string key) => NavigationKey = key;

    public void Use(Page definition)
    {
        Shell = definition.Shell;
        IsPlayer = definition.IsPlayer;
        TitleKey = definition.TitleKey;
        NavigationKey = definition.NavigationKey;
        Content.AddRange(definition.Content);
    }
}
