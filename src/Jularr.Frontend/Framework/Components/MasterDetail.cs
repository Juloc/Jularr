using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

public sealed class MasterDetail : IUiComponent
{
    public List<IUiComponent> Master { get; } = [];
    public List<IUiComponent> Detail { get; } = [];

    public MasterDetail(Action<MasterDetail> define) => define(this);

    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        var children = new List<IUiComponent> { new Section([.. Master]).Class("ui-master") };
        if (Detail.Count > 0) children.Add(new Section([.. Detail]).Class("ui-detail"));
        new Div([.. children])
            .Class(Detail.Count > 0 ? "ui-master-detail" : "ui-master-detail is-master-only")
            .WriteTo(writer, encoder, texts);
    }
}
