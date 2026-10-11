using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

public sealed class UiToolbar(
    bool Search = true, bool Filters = true, bool Sort = true, bool ViewSwitch = false) : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        var controls = new List<IUiComponent>();

        if (Search)
        {
            controls.Add(new Input().Attribute("type", "search").Attribute("disabled", "disabled")
                .Attribute("aria-label", texts["toolbar.search"])
                .Attribute("placeholder", texts["toolbar.search"]));
        }

        void AddButton(string key) => controls.Add(new Button(new Text(key))
            .Attribute("type", "button").Attribute("disabled", "disabled"));

        if (Filters) AddButton("toolbar.filter");
        if (Sort) AddButton("toolbar.sort");
        if (ViewSwitch) AddButton("toolbar.view");

        new Div([.. controls]).Class("page-toolbar")
            .Attribute("aria-label", texts["toolbar.label"]).WriteTo(writer, encoder, texts);
    }
}
