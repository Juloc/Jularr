using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

public sealed record Tab(string Key, IReadOnlyList<IUiComponent> Content);

public sealed class Tabs : IUiComponent
{
    public List<Tab> Items { get; } = [];

    public Tabs(Action<Tabs> define) => define(this);

    public void Add(string key, Action<List<IUiComponent>> define)
    {
        var content = new List<IUiComponent>();
        define(content);
        Items.Add(new Tab(key, content));
    }

    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        foreach (var item in Items)
        {
            var children = new List<IUiComponent> { new H2(new Text(item.Key)) };
            children.AddRange(item.Content);
            new Section([.. children]).Class("page-section").WriteTo(writer, encoder, texts);
        }
    }
}
