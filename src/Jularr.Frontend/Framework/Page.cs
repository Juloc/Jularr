using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Frontend.Framework;

public abstract class JularrPage : PageModel
{
    public Page Definition { get; private set; } = new();

    protected abstract void Define(Page page);

    protected void PreparePage()
    {
        var definition = new Page();
        Define(definition);
        Definition = definition;
    }
}

public sealed class Page
{
    public bool Shell { get; set; } = true;
    public string? TitleKey { get; private set; }
    public string? NavigationKey { get; private set; }
    public string? SectionKey { get; private set; }
    public UiCollection Content { get; } = new();

    public void Title(string key) => TitleKey = key;
    public void Navigation(string key) => NavigationKey = key;
    public void SectionTabs(string key) => SectionKey = key;
}

public interface IUiComponent
{
}

public sealed class UiCollection : List<IUiComponent>
{
}

public sealed class Panel(Action<UiCollection> define) : IUiComponent
{
    public UiCollection Content { get; } = Build(define);

    private static UiCollection Build(Action<UiCollection> define)
    {
        var content = new UiCollection();
        define(content);
        return content;
    }
}

public sealed class MasterDetail : IUiComponent
{
    public UiCollection Master { get; } = new();
    public UiCollection Detail { get; } = new();

    public MasterDetail(Action<MasterDetail> define)
    {
        define(this);
    }
}

public sealed class Tabs : IUiComponent
{
    public List<Tab> Items { get; } = [];

    public Tabs(Action<Tabs> define)
    {
        define(this);
    }

    public void Add(string key, Action<UiCollection> define)
    {
        var content = new UiCollection();
        define(content);
        Items.Add(new Tab(key, content));
    }
}

public sealed record Tab(string Key, UiCollection Content);
