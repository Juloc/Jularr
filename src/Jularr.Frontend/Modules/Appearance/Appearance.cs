using System.Text.Encodings.Web;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Modules.Appearance
{
    public sealed class Appearance(bool collapsible = false) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            // Independent style and brightness.
            var content = new Div(
                Group("appearance.style", "skin", texts, ("original", "appearance.original"), ("clean", "appearance.clean")),
                Group("appearance.brightness", "mode", texts, ("system", "appearance.system"), ("light", "appearance.light"), ("dark", "appearance.dark")))
                .Class("appearance__options");

            if (collapsible)
            {
                new Details(new Summary(new Text("appearance.title")), content).Class("appearance")
                    .WriteTo(writer, encoder, texts);
            }
            else
            {
                content.WriteTo(writer, encoder, texts);
            }
        }

        private static IUiComponent Group(string labelKey, string kind, UiText texts, params (string Value, string LabelKey)[] options) =>
            new Div(new Span(new Text(labelKey)).Class("appearance__label"),
                new Div(options.Select(option => (IUiComponent)new Button(new Text(option.LabelKey))
                    .Attribute("data-set-" + kind, option.Value).Attribute("aria-pressed", "false")).ToArray())
                    .Class("appearance__choices"))
                .Class("appearance__group").Attribute("role", "group").Attribute("aria-label", texts[labelKey]);

        private sealed class Details(params IUiComponent[] content) : HtmlElement("details", content);
        private sealed class Summary(params IUiComponent[] content) : HtmlElement("summary", content);
    }
}
