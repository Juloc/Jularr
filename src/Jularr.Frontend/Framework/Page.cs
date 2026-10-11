using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Frontend.Framework;

public abstract class JularrPage : PageModel
{
    public Page Definition { get; private set; } = new();

    protected abstract void Define(Page page);

    protected void PreparePage() => Definition = BuildDefinition();

    // Preview uses the same real page definitions; it does not execute page handlers or services.
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
    public UiCollection Content { get; } = new();

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

public interface IUiComponent { }

public sealed class UiCollection : List<IUiComponent> { }

public sealed class Panel : IUiComponent
{
    public UiCollection Content { get; } = new();

    public Panel(Action<UiCollection> define) => define(Content);
}

public sealed class MasterDetail : IUiComponent
{
    public UiCollection Master { get; } = new();
    public UiCollection Detail { get; } = new();

    public MasterDetail(Action<MasterDetail> define) => define(this);
}

public sealed class Tabs : IUiComponent
{
    public List<Tab> Items { get; } = new();

    public Tabs(Action<Tabs> define) => define(this);

    public void Add(string key, Action<UiCollection> define)
    {
        var content = new UiCollection();
        define(content);
        Items.Add(new Tab(key, content));
    }
}

public sealed record Tab(string Key, UiCollection Content);
