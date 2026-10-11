using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Ui.Pages;

public abstract class JularrPage : PageModel
{
    public UiPage PageDefinition { get; private set; } = new();

    protected abstract void Define(UiPage page);

    protected void PreparePage()
    {
        var page = new UiPage();
        Define(page);
        PageDefinition = page;
    }
}

public sealed class UiPage
{
    public bool Shell { get; set; } = true;
    public string? NavigationKey { get; private set; }
    public string? BreadcrumbKey { get; private set; }
    public List<IUiPageComponent> Content { get; } = [];

    public void Navigation(string key) => NavigationKey = key;

    public void Breadcrumb(string key) => BreadcrumbKey = key;
}

public interface IUiPageComponent
{
    string PartialName { get; }
    object ViewModel { get; }
}
